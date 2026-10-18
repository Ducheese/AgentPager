package com.agentgrid.mobile.network

import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

internal class OkHttpPhoneSessionTransport : PhoneSessionTransport {
    private val mutableEvents = MutableSharedFlow<TransportEvent>(extraBufferCapacity = 64)
    override val events: Flow<TransportEvent> = mutableEvents
    private val httpClient = OkHttpClient.Builder()
        .pingInterval(20, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        // 连不上局域网时要尽快失败，好切到隧道；默认 10 秒太久，出门在外每次重连都白等。
        .connectTimeout(5, TimeUnit.SECONDS)
        .build()

    @Volatile
    private var socket: WebSocket? = null
    @Volatile
    private var generation = 0L

    private companion object {
        /** 与桥的 LanBridgeServer.SecretHeader 对齐，改一处必须改两处。 */
        const val SECRET_HEADER = "X-AgentGrid-Secret"
        /** 彻底绕过 ngrok 免费域名的浏览器拦截提示页面。 */
        const val NGROK_SKIP_WARNING_HEADER = "ngrok-skip-browser-warning"
    }

    override fun connect(endpoint: PhoneEndpoint) {
        generation += 1
        val currentGeneration = generation
        socket?.cancel()
        val request = runCatching {
            val scheme = if (endpoint.secure) "wss" else "ws"
            val defaultPort = if (endpoint.secure) 443 else 80
            val authority =
                if (endpoint.port == defaultPort) endpoint.host else "${endpoint.host}:${endpoint.port}"
            Request.Builder()
                .url("$scheme://$authority/agentgrid")
                // 桥的握手要验这个头，走隧道时它就是唯一的门（同网段也一样带，逻辑只有一条）。
                .apply { endpoint.secret?.let { header(SECRET_HEADER, it) } }
                .header(NGROK_SKIP_WARNING_HEADER, "1")
                .build()
        }.getOrElse {
            mutableEvents.tryEmit(TransportEvent.Failed(it.message))
            return
        }
        socket = httpClient.newWebSocket(
            request,
            object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    if (accepts(webSocket, currentGeneration)) {
                        mutableEvents.tryEmit(TransportEvent.Opened)
                    }
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    if (accepts(webSocket, currentGeneration)) {
                        mutableEvents.tryEmit(TransportEvent.TextReceived(text))
                    }
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    if (accepts(webSocket, currentGeneration)) {
                        socket = null
                        mutableEvents.tryEmit(TransportEvent.Closed(reason))
                    }
                }

                override fun onFailure(
                    webSocket: WebSocket,
                    t: Throwable,
                    response: Response?,
                ) {
                    if (accepts(webSocket, currentGeneration)) {
                        socket = null
                        mutableEvents.tryEmit(TransportEvent.Failed(t.message))
                    }
                }
            },
        )
    }

    override fun send(text: String): Boolean = socket?.send(text) == true

    override fun disconnect() {
        generation += 1
        socket?.close(1000, "用户断开")
        socket = null
    }

    override fun close() {
        disconnect()
        httpClient.dispatcher.executorService.shutdown()
        httpClient.connectionPool.evictAll()
    }

    private fun accepts(webSocket: WebSocket, eventGeneration: Long): Boolean =
        generation == eventGeneration && socket === webSocket
}
