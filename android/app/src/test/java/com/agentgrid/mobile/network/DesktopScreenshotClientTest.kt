package com.agentgrid.mobile.network

import android.graphics.Bitmap
import java.io.IOException
import kotlinx.coroutines.runBlocking
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class DesktopScreenshotClientTest {

    private fun createFakeBitmap(): Bitmap {
        val unsafeClass = Class.forName("sun.misc.Unsafe")
        val field = unsafeClass.getDeclaredField("theUnsafe")
        field.isAccessible = true
        val unsafe = field.get(null)
        val allocateInstance = unsafeClass.getMethod("allocateInstance", Class::class.java)
        return allocateInstance.invoke(unsafe, Bitmap::class.java) as Bitmap
    }

    private var lastReceivedSecret: String? = null
    private var lastReceivedPath: String? = null
    private var responseCodeToReturn: Int = 200
    private var responseBytesToReturn: ByteArray =
        byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xE0.toByte(), 1, 2, 3, 4)
    private var responseContentType: String = "image/jpeg"
    private var capturedAtHeader: String? = null
    private var shouldThrowIoException: Boolean = false

    private fun createTestClient(): OkHttpClient {
        return OkHttpClient.Builder()
            .addInterceptor(
                Interceptor { chain ->
                    val request = chain.request()
                    lastReceivedPath = request.url.encodedPath
                    lastReceivedSecret = request.header("X-AgentGrid-Secret")

                    if (shouldThrowIoException) {
                        throw IOException("Connection refused for test")
                    }

                    Response.Builder()
                        .request(request)
                        .protocol(Protocol.HTTP_1_1)
                        .code(responseCodeToReturn)
                        .message("OK")
                        .apply { capturedAtHeader?.let { header("X-AgentGrid-Captured-At", it) } }
                        .body(responseBytesToReturn.toResponseBody(responseContentType.toMediaType()))
                        .build()
                },
            )
            .build()
    }

    @Test
    fun `成功请求桌面截图并正确携带配对密钥鉴权头`() = runBlocking {
        val dummyBitmap = createFakeBitmap()
        var decodedBytes: ByteArray? = null
        var ticks = 0

        val client = DesktopScreenshotClient(
            endpoints = {
                listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "test-secret-123"))
            },
            okHttpClient = createTestClient(),
            bitmapDecoder = { bytes ->
                decodedBytes = bytes
                dummyBitmap
            },
            now = { if (ticks++ == 0) 1_000L else 1_500L },
        )

        val result = client.fetchScreenshot()
        assertEquals("/api/desktop/screenshot", lastReceivedPath)
        assertEquals("test-secret-123", lastReceivedSecret)
        assertArrayEquals(
            byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xE0.toByte(), 1, 2, 3, 4),
            decodedBytes,
        )
        // 桥没给快门时间时退回请求发出时间；总耗时从请求前到解码完成。
        assertEquals(1_000L, result.capturedAtMillis)
        assertEquals(500L, result.elapsedMillis)
        assertFalse(result.viaTunnel)
    }

    @Test
    fun `桥端快门时间优先且隧道端点标记为 TUNNEL`() = runBlocking {
        capturedAtHeader = "1793632728067"
        val client = DesktopScreenshotClient(
            endpoints = {
                listOf(PhoneEndpoint(host = "tunnel.test", port = 443, secure = true, secret = "s"))
            },
            okHttpClient = createTestClient(),
            bitmapDecoder = { createFakeBitmap() },
            now = { 9_000L },
        )

        val result = client.fetchScreenshot()
        assertEquals(1_793_632_728_067L, result.capturedAtMillis)
        assertEquals(0L, result.elapsedMillis)
        assertTrue(result.viaTunnel)
    }

    @Test
    fun `服务端返回401时抛出认证失败异常`() {
        responseCodeToReturn = 401
        responseBytesToReturn = "Unauthorized".toByteArray()

        val client = DesktopScreenshotClient(
            endpoints = {
                listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "wrong-secret"))
            },
            okHttpClient = createTestClient(),
        )

        val ex = assertThrows(IllegalStateException::class.java) {
            runBlocking { client.fetchScreenshot() }
        }
        assertTrue(ex.message!!.contains("401"))
    }

    @Test
    fun `服务端返回503时抛出锁屏不可用提示`() {
        responseCodeToReturn = 503
        responseBytesToReturn = "Service Unavailable".toByteArray()

        val client = DesktopScreenshotClient(
            endpoints = {
                listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "test-secret"))
            },
            okHttpClient = createTestClient(),
        )

        val ex = assertThrows(IllegalStateException::class.java) {
            runBlocking { client.fetchScreenshot() }
        }
        assertTrue(ex.message!!.contains("503") || ex.message!!.contains("锁屏"))
    }

    @Test
    fun `非 JPEG 内容类型直接失败且不标记为可重试`() {
        responseCodeToReturn = 200
        responseContentType = "text/html"
        responseBytesToReturn = "<html>gateway error</html>".toByteArray()

        val client = DesktopScreenshotClient(
            endpoints = { listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "test-secret")) },
            okHttpClient = createTestClient(),
        )

        val ex = assertThrows(DesktopScreenshotException::class.java) {
            runBlocking { client.fetchScreenshot() }
        }
        assertFalse(ex.retryable)
        assertTrue(ex.message!!.contains("JPEG"))
    }

    @Test
    fun `无 JPEG 魔数的数据直接失败且不标记为可重试`() {
        responseCodeToReturn = 200
        responseBytesToReturn = byteArrayOf(1, 2, 3, 4)

        val client = DesktopScreenshotClient(
            endpoints = { listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "test-secret")) },
            okHttpClient = createTestClient(),
        )

        val ex = assertThrows(DesktopScreenshotException::class.java) {
            runBlocking { client.fetchScreenshot() }
        }
        assertFalse(ex.retryable)
        assertTrue(ex.message!!.contains("有效 JPEG"))
    }

    @Test
    fun `超过 64MB 的响应在读取前被拒绝`() {
        val oversizedBody = object : ResponseBody() {
            override fun contentType() = "image/jpeg".toMediaType()
            override fun contentLength(): Long = 64L * 1024 * 1024 + 1
            override fun source() = Buffer()
        }
        val httpClient = OkHttpClient.Builder()
            .addInterceptor { chain ->
                Response.Builder()
                    .request(chain.request())
                    .protocol(Protocol.HTTP_1_1)
                    .code(200)
                    .message("OK")
                    .body(oversizedBody)
                    .build()
            }
            .build()

        val client = DesktopScreenshotClient(
            endpoints = { listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "test-secret")) },
            okHttpClient = httpClient,
        )

        val ex = assertThrows(DesktopScreenshotException::class.java) {
            runBlocking { client.fetchScreenshot() }
        }
        assertFalse(ex.retryable)
        assertTrue(ex.message!!.contains("过大") || ex.message!!.contains("上限"))
    }

    @Test
    fun `404 返回升级桥程序提示`() {
        responseCodeToReturn = 404
        responseBytesToReturn = "Not Found".toByteArray()

        val client = DesktopScreenshotClient(
            endpoints = { listOf(PhoneEndpoint(host = "127.0.0.1", port = 8765, secret = "test-secret")) },
            okHttpClient = createTestClient(),
        )

        val ex = assertThrows(DesktopScreenshotException::class.java) {
            runBlocking { client.fetchScreenshot() }
        }
        assertFalse(ex.retryable)
        assertTrue(ex.message!!.contains("404"))
    }

    @Test
    fun `503 等多端点时不再触发故障转移`() = runBlocking {
        var requestCount = 0
        val client = OkHttpClient.Builder()
            .addInterceptor { chain ->
                requestCount++
                val request = chain.request()
                Response.Builder()
                    .request(request)
                    .protocol(Protocol.HTTP_1_1)
                    .code(503)
                    .message("Service Unavailable")
                    .body("locked".toResponseBody("text/plain".toMediaType()))
                    .build()
            }
            .build()

        val screenshotClient = DesktopScreenshotClient(
            endpoints = {
                listOf(
                    PhoneEndpoint(host = "192.168.1.10", port = 8765, secret = "endpoint-1"),
                    PhoneEndpoint(host = "10.0.0.2", port = 8765, secret = "endpoint-2"),
                )
            },
            okHttpClient = client,
        )

        val ex = assertThrows(DesktopScreenshotException::class.java) {
            runBlocking { screenshotClient.fetchScreenshot() }
        }
        assertEquals(1, requestCount)
        assertFalse(ex.retryable)
        assertTrue(ex.message!!.contains("503") || ex.message!!.contains("锁屏"))
    }

    @Test
    fun `第一个端点失败时自动故障转移重试第二个端点`() = runBlocking {
        val dummyBitmap = createFakeBitmap()
        var requestCount = 0

        val failoverOkHttpClient = OkHttpClient.Builder()
            .addInterceptor(
                Interceptor { chain ->
                    requestCount++
                    val request = chain.request()
                    if (requestCount == 1) {
                        throw IOException("Simulated network failure on candidate 1")
                    }
                    lastReceivedSecret = request.header("X-AgentGrid-Secret")
                    Response.Builder()
                        .request(request)
                        .protocol(Protocol.HTTP_1_1)
                        .code(200)
                        .message("OK")
                        .body(
                            byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xE0.toByte(), 10, 20)
                                .toResponseBody("image/jpeg".toMediaType()),
                        )
                        .build()
                },
            )
            .build()

        val client = DesktopScreenshotClient(
            endpoints = {
                listOf(
                    PhoneEndpoint(host = "192.168.1.10", port = 8765, secret = "endpoint-1"),
                    PhoneEndpoint(host = "10.0.0.2", port = 8765, secret = "endpoint-2"),
                )
            },
            okHttpClient = failoverOkHttpClient,
            bitmapDecoder = { dummyBitmap },
        )

        val result = client.fetchScreenshot()
        assertNotNull(result)
        assertEquals(2, requestCount)
        assertEquals("endpoint-2", lastReceivedSecret)
    }
}
