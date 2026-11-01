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
 * 复用 PhoneSession 的候选地址与配对密钥鉴权（局域网优先，隧道兜底）。
 */
class DesktopScreenshotException(
    message: String,
    val retryable: Boolean,
    cause: Throwable? = null,
) : IllegalStateException(message, cause)

class DesktopScreenshotClient(
    private val endpoints: suspend () -> List<PhoneEndpoint>,
    private val okHttpClient: OkHttpClient = defaultClient(),
    private val bitmapDecoder: (ByteArray) -> Bitmap? = { bytes ->
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
    },
) {
    companion object {
        private const val MAX_IMAGE_BYTES = 64L * 1024 * 1024

        fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(15, TimeUnit.SECONDS)
            .retryOnConnectionFailure(true)
            .build()
    }

    suspend fun fetchScreenshot(): Bitmap {
        val candidates = endpoints()
        if (candidates.isEmpty()) {
            throw IllegalStateException("未连接或未配对，没有可用的服务端地址")
        }

        var lastError: Exception? = null
        for (endpoint in candidates) {
            try {
                return fetchFrom(endpoint)
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

    private suspend fun fetchFrom(endpoint: PhoneEndpoint): Bitmap {
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
            val call = okHttpClient.newCall(request)
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
                                continuation.resume(bitmap)
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
}
