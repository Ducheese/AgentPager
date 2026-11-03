package com.agentgrid.mobile.attachments

import com.agentgrid.mobile.network.PhoneEndpoint
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.json.Json
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response

/**
 * 图片二进制直传 Bridge 暂存区（POST /api/attachments/upload）。
 * 候选顺序跟随当前 WS 连接（蜂窝下只有隧道），握手头与 WS 一致。
 * 用 enqueue + invokeOnCancellation：删图取消协程时立刻 abort 请求，不留孤儿文件。
 */
class AttachmentUploader(
    private val endpoints: suspend () -> List<PhoneEndpoint>,
    private val json: Json = Json { ignoreUnknownKeys = true },
) {
    /** 候选轮换由调用方负责；OkHttp 自己重试只会把局域网失败拖得更久，POST 重发还可能多落一份。 */
    private val baseHttp = OkHttpClient.Builder()
        .writeTimeout(30, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .retryOnConnectionFailure(false)
        .build()

    /** 局域网路由不通时快速失败；隧道在移动网络下 TLS 建连可能偏慢。 */
    private val lanHttp = baseHttp.newBuilder()
        .connectTimeout(2, TimeUnit.SECONDS)
        .build()
    private val tunnelHttp = baseHttp.newBuilder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .build()

    suspend fun upload(bytes: ByteArray, fileName: String, meta: String? = null): AttachmentUploadResponse {
        val candidates = endpoints()
        var lastError: Exception? = null
        for (endpoint in candidates) {
            try {
                return uploadTo(endpoint, bytes, fileName, meta)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                lastError = error
            }
        }
        throw lastError ?: IllegalStateException("没有可用的上传地址")
    }

    private suspend fun uploadTo(
        endpoint: PhoneEndpoint,
        bytes: ByteArray,
        fileName: String,
        meta: String?,
    ): AttachmentUploadResponse {
        val scheme = if (endpoint.secure) "https" else "http"
        val defaultPort = if (endpoint.secure) 443 else 80
        val authority = if (endpoint.port == defaultPort) endpoint.host else "${endpoint.host}:${endpoint.port}"
        val multipart = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
            .addFormDataPart(
                "file",
                fileName,
                bytes.toRequestBody("image/jpeg".toMediaType()),
            )
        if (!meta.isNullOrBlank()) multipart.addFormDataPart("meta", meta)
        val request = Request.Builder()
            .url("$scheme://$authority/api/attachments/upload")
            .apply { endpoint.secret?.let { header("X-AgentGrid-Secret", it) } }
            .header("ngrok-skip-browser-warning", "1")
            .post(multipart.build())
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
                            val text = response.body?.string().orEmpty()
                            if (!response.isSuccessful) {
                                throw IllegalStateException("上传失败（HTTP ${response.code}）：${text.take(200)}")
                            }
                            if (!continuation.isCancelled) {
                                continuation.resume(
                                    json.decodeFromString(AttachmentUploadResponse.serializer(), text),
                                )
                            }
                        } catch (error: Exception) {
                            if (!continuation.isCancelled) continuation.resumeWithException(error)
                        }
                    }
                }
            })
        }
    }
}
