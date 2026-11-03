package com.agentgrid.mobile.network

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import okhttp3.Call
import okhttp3.Callback
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody

/**
 * 桌面屏幕截图客户端（GET /api/desktop/screenshot）。
 * 候选顺序由 [PhoneSession.uploadEndpoints] 给出：当前 WS 所在腿排第一，蜂窝下不含局域网。
 */
class DesktopScreenshotException(
    message: String,
    val retryable: Boolean,
    cause: Throwable? = null,
) : IllegalStateException(message, cause)

/** 一次截屏的完整结果：位图 + 桥端快门时间 + 实际走的腿 + 总耗时。 */
data class DesktopScreenshotResult(
    val bitmap: Bitmap,
    val capturedAtMillis: Long,
    val viaTunnel: Boolean,
    val elapsedMillis: Long,
)

class DesktopScreenshotClient(
    private val endpoints: suspend () -> List<PhoneEndpoint>,
    private val okHttpClient: OkHttpClient = defaultClient(),
    private val bitmapDecoder: (ByteArray) -> Bitmap? = { bytes ->
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
    },
    private val now: () -> Long = System::currentTimeMillis,
) {
    companion object {
        private const val MAX_IMAGE_BYTES = 64L * 1024 * 1024

        /** 桥端在按下快门时写入的 Unix 毫秒；旧桥没有这个头。 */
        private const val CAPTURED_AT_HEADER = "X-AgentGrid-Captured-At"

        fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .readTimeout(30, TimeUnit.SECONDS)
            // 候选轮换由本类负责；OkHttp 自己重试只会把局域网失败拖得更久。
            .retryOnConnectionFailure(false)
            .build()
    }

    /** 局域网路由不通时快速失败；连上后不回包也不能拖到 30s。 */
    private val lanHttp = okHttpClient.newBuilder()
        .connectTimeout(2, TimeUnit.SECONDS)
        .readTimeout(5, TimeUnit.SECONDS)
        .build()
    private val tunnelHttp = okHttpClient.newBuilder()
        .connectTimeout(10, TimeUnit.SECONDS)
        // GET 截图可安全重试，保留 Cloudflare/ngrok 多 Anycast/IP 容灾。
        .retryOnConnectionFailure(true)
        .build()

    suspend fun fetchScreenshot(): DesktopScreenshotResult {
        val startedAt = now()
        val candidates = endpoints()
        if (candidates.isEmpty()) {
            throw IllegalStateException("未连接或未配对，没有可用的服务端地址")
        }

        var lastError: Exception? = null
        for (endpoint in candidates) {
            try {
                val fetched = fetchFrom(endpoint)
                return DesktopScreenshotResult(
                    bitmap = fetched.bitmap,
                    capturedAtMillis = fetched.capturedAtMillis ?: startedAt,
                    viaTunnel = endpoint.secure,
                    elapsedMillis = now() - startedAt,
                )
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: DesktopScreenshotException) {
                if (!error.retryable) throw error
                lastError = error
            } catch (error: IOException) {
                lastError = error
            }
        }
        throw lastError ?: IllegalStateException("获取桌面截图失败：所有服务端地址均不可达")
    }

    private suspend fun fetchFrom(endpoint: PhoneEndpoint): FetchedScreenshot {
        val scheme = if (endpoint.secure) "https" else "http"
        val defaultPort = if (endpoint.secure) 443 else 80
        val authority = if (endpoint.port == defaultPort) endpoint.host else "${endpoint.host}:${endpoint.port}"

        val request = Request.Builder()
            .url("$scheme://$authority/api/desktop/screenshot")
            .apply { endpoint.secret?.let { header("X-AgentGrid-Secret", it) } }
            .header("ngrok-skip-browser-warning", "1")
            .header("Cache-Control", "no-cache")
            .get()
            .build()

        return suspendCancellableCoroutine { continuation ->
            val client = if (endpoint.secure) tunnelHttp else lanHttp
            val call = client.newCall(request)
            continuation.invokeOnCancellation { call.cancel() }
            call.enqueue(object : Callback {
                override fun onFailure(call: Call, e: IOException) {
                    if (!continuation.isCancelled) continuation.resumeWithException(e)
                }

                override fun onResponse(call: Call, response: Response) {
                    response.use {
                        try {
                            if (!response.isSuccessful) {
                                val message = when (response.code) {
                                    401 -> "桌面截图认证失败：配对密钥不匹配 (401)"
                                    404 -> "Windows 桥端暂不支持桌面截图功能，请升级桥程序 (404)"
                                    503 -> "桌面截图不可用：可能处于锁屏或无活动显示器 (503)"
                                    else -> "获取桌面截图失败 (HTTP ${response.code})"
                                }
                                throw DesktopScreenshotException(message, retryable = false)
                            }

                            val body = response.body
                                ?: throw DesktopScreenshotException("服务端未返回图片数据", retryable = false)
                            val mediaType = body.contentType()
                            if (mediaType?.type != "image" ||
                                mediaType.subtype !in setOf("jpeg", "jpg")
                            ) {
                                throw DesktopScreenshotException(
                                    "服务端返回了非 JPEG 内容：${mediaType ?: "未知类型"}",
                                    retryable = false,
                                )
                            }

                            val bytes = readBodyLimited(body)
                            if (!isJpeg(bytes)) {
                                throw DesktopScreenshotException(
                                    "服务端返回的图片不是有效 JPEG",
                                    retryable = false,
                                )
                            }
                            val bitmap = bitmapDecoder(bytes)
                                ?: throw DesktopScreenshotException(
                                    "解码屏幕截图图片数据失败",
                                    retryable = false,
                                )

                            if (!continuation.isCancelled) {
                                continuation.resume(
                                    FetchedScreenshot(
                                        bitmap = bitmap,
                                        capturedAtMillis = response.header(CAPTURED_AT_HEADER)
                                            ?.toLongOrNull(),
                                    ),
                                )
                            }
                        } catch (error: DesktopScreenshotException) {
                            if (!continuation.isCancelled) continuation.resumeWithException(error)
                        } catch (error: IOException) {
                            if (!continuation.isCancelled) continuation.resumeWithException(error)
                        } catch (error: Exception) {
                            if (!continuation.isCancelled) {
                                continuation.resumeWithException(
                                    DesktopScreenshotException(
                                        error.message ?: "读取桌面截图失败",
                                        retryable = false,
                                        cause = error,
                                    ),
                                )
                            }
                        }
                    }
                }
            })
        }
    }

    private fun readBodyLimited(body: ResponseBody): ByteArray {
        val declared = body.contentLength()
        if (declared > MAX_IMAGE_BYTES) {
            throw IllegalStateException("桌面截图过大：$declared 字节")
        }

        val initialCapacity = if (declared in 1..MAX_IMAGE_BYTES) {
            declared.toInt()
        } else {
            16 * 1024
        }
        val output = ByteArrayOutputStream(initialCapacity)
        body.byteStream().use { input ->
            val buffer = ByteArray(16 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                if (output.size().toLong() + read > MAX_IMAGE_BYTES) {
                    throw IllegalStateException("桌面截图超过大小上限")
                }
                output.write(buffer, 0, read)
            }
        }
        return output.toByteArray()
    }

    private fun isJpeg(bytes: ByteArray): Boolean {
        return bytes.size >= 3 &&
            bytes[0] == 0xFF.toByte() &&
            bytes[1] == 0xD8.toByte() &&
            bytes[2] == 0xFF.toByte()
    }

    private data class FetchedScreenshot(
        val bitmap: Bitmap,
        val capturedAtMillis: Long?,
    )
}
