package com.agentgrid.mobile.network

import com.agentgrid.mobile.domain.ControlAckPayload
import com.agentgrid.mobile.domain.ControlAction
import com.agentgrid.mobile.domain.ControlPayload
import com.agentgrid.mobile.domain.ControlResult
import com.agentgrid.mobile.domain.PairingPayload
import com.agentgrid.mobile.domain.SignedControlEnvelope
import com.agentgrid.mobile.domain.StateSnapshotPayload
import java.net.InetSocketAddress
import java.net.Socket
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.decodeFromJsonElement

enum class LinkState {
    DISCONNECTED,
    CONNECTING,
    CONNECTED,
}

/** 当前桥连接走的腿；截图/设置页只展示，不参与鉴权。 */
enum class BridgeRoute(val label: String) {
    LAN("LAN"),
    TUNNEL("TUNNEL"),
}

/**
 * 手机默认网络对桥的局域网地址意味着什么。
 * Remote = 蜂窝等非本地传输：局域网不可能可达，候选里直接不生成它，避免每次请求先等连接超时。
 */
enum class NetworkKind {
    Unknown,
    Local,
    Remote,
}

data class PairingSummary(
    val serviceID: String,
    val host: String,
    val port: Int,
)

sealed interface PhoneSessionProblem {
    data object InvalidPairing : PhoneSessionProblem
    data object UnsupportedVersion : PhoneSessionProblem
    data object InvalidEndpoint : PhoneSessionProblem
    data object InvalidSecret : PhoneSessionProblem
    data object NotPaired : PhoneSessionProblem
    data object NotConnected : PhoneSessionProblem
    data object CredentialFailure : PhoneSessionProblem
    data object SequenceFailure : PhoneSessionProblem
    data object TransportFailure : PhoneSessionProblem
    data object ProtocolFailure : PhoneSessionProblem
}

sealed interface PairingOutcome {
    data object Connecting : PairingOutcome
    data class Rejected(val problem: PhoneSessionProblem) : PairingOutcome
}

sealed interface ControlSubmission {
    data class Queued(val requestID: String) : ControlSubmission
    data class Rejected(val problem: PhoneSessionProblem) : ControlSubmission
}

sealed interface ControlDelivery {
    data class Pending(val submittedAt: Long) : ControlDelivery
    data class Acknowledged(
        val result: ControlResult,
        val reason: String?,
    ) : ControlDelivery

    data object TimedOut : ControlDelivery
    data object SendFailed : ControlDelivery
}

data class PhoneSessionState(
    val pairing: PairingSummary? = null,
    val link: LinkState = LinkState.DISCONNECTED,
    /** 当前连接实际走的腿；未连接时为 null。设置页的 LINK 行用它显示 LAN/TUNNEL。 */
    val linkRoute: BridgeRoute? = null,
    val snapshot: StateSnapshotPayload = StateSnapshotPayload(emptyList()),
    val controls: Map<String, ControlDelivery> = emptyMap(),
    val problem: PhoneSessionProblem? = null,
)

interface PhoneSession : AutoCloseable {
    val state: StateFlow<PhoneSessionState>

    suspend fun start()

    suspend fun pair(encodedPairing: String): PairingOutcome

    suspend fun control(
        taskID: String,
        action: ControlAction,
        value: String? = null,
        values: List<String>? = null,
    ): ControlSubmission

    suspend fun unpair()

    /**
     * 从后台冻结/断网恢复时的立即重连：取消退避等待、清零计数、马上再连当前候选。
     * 已连接或没有配对时什么都不做（幂等，可在回前台/网络恢复/亮屏时随手调用）。
     */
    fun nudge()

    /**
     * 默认网络变了（WiFi 连上/断开、切蜂窝、回前台/亮屏）时调用。
     * 按当前网络类型与局域网可达性调整真实连接：
     * - 切到蜂窝：立即放弃局域网、改连隧道；
     * - WiFi 回来：先探测局域网，通才从隧道切回，避免异地 WiFi 把可用隧道掐掉；
     * - 未连接：按探测结果直接选 LAN 或 TUNNEL。
     * 幂等，可在网络回调、回前台、亮屏时随手调用。
     */
    fun onNetworkChanged()

    /** 网络恢复/回前台等必须重排的场景：跳过同网络类型去重，强制重新探测/选路。 */
    fun forceNetworkChanged()

    /**
     * 附件上传/桌面截图用的候选地址（含配对密钥）。顺序跟随当前连接与网络类型：
     * 当前腿排第一，蜂窝下不含局域网候选。未配对时返回空列表。
     */
    suspend fun uploadEndpoints(): List<PhoneEndpoint>
}

internal interface PhoneCredentialStore {
    suspend fun load(): PairingPayload?

    suspend fun save(pairing: PairingPayload)

    suspend fun clear()

    suspend fun reserveNextSequence(): ULong
}

data class PhoneEndpoint(
    val host: String,
    val port: Int,
    /** 走 wss 而不是 ws —— 隧道那头是 443/TLS。 */
    val secure: Boolean = false,
    /** 握手要带的配对密钥（桥用 X-AgentGrid-Secret 校验），局域网也带。 */
    val secret: String? = null,
) {
    val route: BridgeRoute
        get() = if (secure) BridgeRoute.TUNNEL else BridgeRoute.LAN
}

internal sealed interface TransportEvent {
    data object Opened : TransportEvent
    data class TextReceived(val text: String) : TransportEvent
    data class Closed(val reason: String?) : TransportEvent
    data class Failed(val reason: String?) : TransportEvent
}

internal interface PhoneSessionTransport : AutoCloseable {
    val events: kotlinx.coroutines.flow.Flow<TransportEvent>

    fun connect(endpoint: PhoneEndpoint)

    fun send(text: String): Boolean

    fun disconnect()
}

internal class DefaultPhoneSession(
    private val credentialStore: PhoneCredentialStore,
    private val transport: PhoneSessionTransport,
    private val deviceID: String,
    private val scope: CoroutineScope,
    private val now: () -> Long = System::currentTimeMillis,
    private val requestID: () -> String = { UUID.randomUUID().toString() },
    private val nonce: () -> String = { UUID.randomUUID().toString() },
    private val wait: suspend (Long) -> Unit = { delay(it) },
    private val acknowledgmentTimeoutMillis: Long = 15_000L,
    /** 当前默认网络类型；生产由 PhoneSessionFactory 接 ConnectivityManager，单测默认 Unknown。 */
    private val networkKind: () -> NetworkKind = { NetworkKind.Unknown },
    /** 切回局域网前的可达性探测；默认请求桥的 /api/ping，测试可注入。 */
    private val probeLocal: suspend (PhoneEndpoint) -> Boolean = { endpoint ->
        withContext(Dispatchers.IO) {
            runCatching {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(endpoint.host, endpoint.port), PROBE_TIMEOUT_MS)
                    socket.soTimeout = PROBE_TIMEOUT_MS
                    val secret = endpoint.secret
                        ?.let { "X-AgentGrid-Secret: $it\r\n" }
                        .orEmpty()
                    val request = "GET /api/ping HTTP/1.1\r\n" +
                        "Host: ${endpoint.host}:${endpoint.port}\r\n" +
                        secret +
                        "Connection: close\r\n\r\n"
                    socket.getOutputStream().apply {
                        write(request.toByteArray(Charsets.US_ASCII))
                        flush()
                    }
                    val reader = socket.getInputStream().bufferedReader(Charsets.US_ASCII)
                    val status = reader.readLine() ?: return@runCatching false
                    var hasMarker = false
                    while (true) {
                        val line = reader.readLine() ?: break
                        if (line.isEmpty()) break
                        if (line.startsWith("X-AgentGrid-Ping:", ignoreCase = true) &&
                            line.substringAfter(':').trim() == "1"
                        ) {
                            hasMarker = true
                        }
                    }
                    status.startsWith("HTTP/1.1 204") && hasMarker
                }
            }.getOrDefault(false)
        }
    },
) : PhoneSession {
    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        explicitNulls = false
    }
    private val mutex = Mutex()
    private val mutableState = MutableStateFlow(PhoneSessionState())
    override val state: StateFlow<PhoneSessionState> = mutableState.asStateFlow()

    private var pairing: PairingPayload? = null
    private var started = false
    @Volatile
    private var stopped = false
    private var retryCount = 0
    private var reconnectJob: Job? = null
    private val tuneLock = Any()
    /** 重配对代次；探测回来时代次不一致就丢弃，避免旧 LAN 结果作用到新配对。 */
    @Volatile
    private var pairingGeneration = 0
    /** 候选地址：局域网在前、隧道在后。连不上就轮换到下一个（见 disconnectAndRetryLocked）。 */
    private var endpoints: List<PhoneEndpoint> = emptyList()
    private var endpointIndex = 0
    /** 本次 connect 实际拨的地址；Opened 时据此记录 linkRoute，上传也据此排第一。 */
    private var connectingEndpoint: PhoneEndpoint? = null
    @Volatile
    private var networkTuneJob: Job? = null
    /** 当前排队的选路任务对应的网络类型；同类型回调只保留一份，避免 600ms 补调掐掉在飞探测。 */
    @Volatile
    private var networkTuneKind: NetworkKind? = null
    /** 局域网刚失败过：隧道连上后先别自动探测升级，免得在坏 LAN 上来回切。 */
    private var localUpgradeBlockedUntil = 0L
    private var lastSnapshotSentAt = Long.MIN_VALUE
    private var lastSnapshotMessageID: String? = null
    private val timeoutJobs = mutableMapOf<String, Job>()

    init {
        scope.launch {
            transport.events.collect(::acceptTransportEvent)
        }
    }

    override suspend fun start() {
        mutex.withLock {
            if (started) return
            started = true
            stopped = false
            val restored = runCatching { credentialStore.load() }.getOrElse {
                runCatching { credentialStore.clear() }
                mutableState.value = mutableState.value.copy(
                    problem = PhoneSessionProblem.CredentialFailure,
                )
                return
            }
            if (restored != null && validatePairing(restored) != null) {
                runCatching { credentialStore.clear() }
                mutableState.value = PhoneSessionState(
                    problem = PhoneSessionProblem.CredentialFailure,
                )
                return
            }
            pairing = restored
            if (restored == null) {
                mutableState.value = PhoneSessionState()
            } else {
                mutableState.value = mutableState.value.copy(
                    pairing = restored.summary,
                    problem = null,
                )
                connectLocked(restored)
            }
        }
    }

    override suspend fun pair(encodedPairing: String): PairingOutcome {
        val decoded = runCatching {
            json.decodeFromString<PairingPayload>(encodedPairing.trim())
        }.getOrElse {
            return PairingOutcome.Rejected(PhoneSessionProblem.InvalidPairing)
        }
        validatePairing(decoded)?.let { return PairingOutcome.Rejected(it) }

        return mutex.withLock {
            val saved = runCatching { credentialStore.save(decoded) }.isSuccess
            if (!saved) {
                return@withLock PairingOutcome.Rejected(PhoneSessionProblem.CredentialFailure)
            }
            stopped = false
            started = true
            reconnectJob?.cancel()
            reconnectJob = null
            cancelTune()
            transport.disconnect()
            pairing = decoded
            pairingGeneration++
            retryCount = 0
            localUpgradeBlockedUntil = 0L
            // 重新配对等于重新开始：候选从头走（能扫到码就说明人在局域网里）
            endpoints = emptyList()
            endpointIndex = 0
            connectingEndpoint = null
            mutableState.value = PhoneSessionState(
                pairing = decoded.summary,
                link = LinkState.CONNECTING,
            )
            connectLocked(decoded)
            requestTune(force = true)
            PairingOutcome.Connecting
        }
    }

    override suspend fun control(
        taskID: String,
        action: ControlAction,
        value: String?,
        values: List<String>?,
    ): ControlSubmission = mutex.withLock {
        val currentPairing = pairing
            ?: return@withLock ControlSubmission.Rejected(PhoneSessionProblem.NotPaired)
        if (mutableState.value.link != LinkState.CONNECTED) {
            return@withLock ControlSubmission.Rejected(PhoneSessionProblem.NotConnected)
        }
        val sequence = runCatching { credentialStore.reserveNextSequence() }
            .getOrElse {
                return@withLock ControlSubmission.Rejected(
                    PhoneSessionProblem.SequenceFailure,
                )
            }
        val id = requestID()
        val unsigned = SignedControlEnvelope(
            messageId = id,
            sentAt = now(),
            deviceId = deviceID,
            sequence = sequence,
            nonce = nonce(),
            payload = ControlPayload(taskID, action, value, values),
            signature = "",
        )
        val secret = runCatching { Base64.getDecoder().decode(currentPairing.secret) }
            .getOrElse {
                return@withLock ControlSubmission.Rejected(PhoneSessionProblem.InvalidSecret)
            }
        val signed = unsigned.copy(signature = ControlSigner.sign(unsigned, secret))
        val encoded = runCatching { json.encodeToString(signed) }
            .getOrElse {
                return@withLock ControlSubmission.Rejected(
                    PhoneSessionProblem.ProtocolFailure,
                )
            }
        updateControl(id, ControlDelivery.Pending(unsigned.sentAt))
        if (!transport.send(encoded)) {
            updateControl(id, ControlDelivery.SendFailed)
            return@withLock ControlSubmission.Rejected(PhoneSessionProblem.TransportFailure)
        }
        timeoutJobs[id] = scope.launch {
            wait(acknowledgmentTimeoutMillis)
            mutex.withLock {
                if (mutableState.value.controls[id] is ControlDelivery.Pending) {
                    updateControl(id, ControlDelivery.TimedOut)
                }
                timeoutJobs.remove(id)
            }
        }
        ControlSubmission.Queued(id)
    }

    override suspend fun unpair() {
        mutex.withLock {
            stopped = true
            reconnectJob?.cancel()
            reconnectJob = null
            cancelTune()
            connectingEndpoint = null
            timeoutJobs.values.forEach(Job::cancel)
            timeoutJobs.clear()
            transport.disconnect()
            val cleared = runCatching { credentialStore.clear() }.isSuccess
            if (!cleared) {
                stopped = false
                mutableState.value = mutableState.value.copy(
                    link = LinkState.DISCONNECTED,
                    problem = PhoneSessionProblem.CredentialFailure,
                )
                return@withLock
            }
            pairing = null
            retryCount = 0
            lastSnapshotSentAt = Long.MIN_VALUE
            lastSnapshotMessageID = null
            mutableState.value = PhoneSessionState()
        }
    }

    override fun nudge() {
        scope.launch {
            mutex.withLock { reconnectNowLocked() }
        }
    }

    override fun onNetworkChanged() = requestTune(force = false)

    override fun forceNetworkChanged() = requestTune(force = true)

    /**
     * 同一网络类型的重复回调不重启探测；强制路径用于网络恢复/Opened 补探/配对，
     * 必须允许覆盖同类型的在飞任务。
     */
    private fun requestTune(force: Boolean) {
        if (stopped) return
        val kind = networkKind()
        synchronized(tuneLock) {
            if (stopped) return
            if (!force && networkTuneJob?.isActive == true && networkTuneKind == kind) return
            networkTuneKind = kind
            val generation = pairingGeneration
            networkTuneJob?.cancel()
            networkTuneJob = scope.launch { tuneRoute(kind, generation) }
        }
    }

    private fun cancelTune() {
        synchronized(tuneLock) {
            networkTuneJob?.cancel()
            networkTuneJob = null
            networkTuneKind = null
        }
    }

    override fun close() {
        stopped = true
        reconnectJob?.cancel()
        cancelTune()
        timeoutJobs.values.forEach(Job::cancel)
        transport.disconnect()
        transport.close()
        scope.cancel()
    }

    override suspend fun uploadEndpoints(): List<PhoneEndpoint> = mutex.withLock {
        val current = pairing ?: return emptyList()
        val candidates = endpointsFor(current)
        // 以当前连接（或正在拨的）那条腿为准；否则手机已经落在隧道上，
        // 截图/附件还会先对着局域网空等 connect timeout。
        val active = activeEndpointLocked(candidates) ?: return candidates
        listOf(active) + candidates.filter { it != active }
    }

    private suspend fun acceptTransportEvent(event: TransportEvent) {
        mutex.withLock {
            when (event) {
                TransportEvent.Opened -> {
                    retryCount = 0
                    reconnectJob?.cancel()
                    reconnectJob = null
                    val endpoint = connectingEndpoint ?: endpoints.getOrNull(endpointIndex)
                    mutableState.value = mutableState.value.copy(
                        link = LinkState.CONNECTED,
                        linkRoute = endpoint?.route,
                        problem = null,
                    )
                    // 网络回调可能发生在隧道还在握手时，那次升级探测被跳过；
                    // 现在隧道已经连上，WiFi 环境下补一次 LAN 探测，通就切回局域网。
                    // 局域网刚失败过的冷却期内不补探，避免在坏 LAN 上反复切换。
                    if (endpoint?.secure == true &&
                        networkKind() == NetworkKind.Local &&
                        now() >= localUpgradeBlockedUntil
                    ) {
                        requestTune(force = true)
                    }
                }

                is TransportEvent.TextReceived -> acceptMessageLocked(event.text)
                // 断连说明这个地址本来是通的，保持不动；只有连不上（初始握手阶段）才换候选。
                is TransportEvent.Closed -> disconnectAndRetryLocked(advanceEndpoint = false)
                is TransportEvent.Failed -> {
                    // 若断开前已经是 CONNECTED 状态，说明当前端点本是可达的，移动网络抖动/切基站/丢包重连不应轮换端点；
                    // 仅当从未连上（初次握手阶段失败）时才轮换下一个候选端点。
                    val wasConnected = mutableState.value.link == LinkState.CONNECTED
                    disconnectAndRetryLocked(advanceEndpoint = !wasConnected)
                }
            }
        }
    }

    private suspend fun acceptMessageLocked(text: String) {
        val envelope = runCatching { json.decodeFromString<IncomingEnvelope>(text) }
            .getOrElse {
                mutableState.value = mutableState.value.copy(
                    problem = PhoneSessionProblem.ProtocolFailure,
                )
                return
            }
        if (envelope.version != 1) {
            mutableState.value = mutableState.value.copy(
                problem = PhoneSessionProblem.ProtocolFailure,
            )
            return
        }
        when (envelope.type) {
            "state.snapshot" -> {
                if (
                    envelope.sentAt < lastSnapshotSentAt ||
                    envelope.messageId == lastSnapshotMessageID
                ) {
                    return
                }
                val snapshot = runCatching {
                    json.decodeFromJsonElement<StateSnapshotPayload>(envelope.payload)
                }.getOrElse {
                    mutableState.value = mutableState.value.copy(
                        problem = PhoneSessionProblem.ProtocolFailure,
                    )
                    return
                }
                lastSnapshotSentAt = envelope.sentAt
                lastSnapshotMessageID = envelope.messageId
                applyTunnelHostLocked(snapshot.activeTunnelHost)
                mutableState.value = mutableState.value.copy(
                    snapshot = snapshot,
                    problem = null,
                )
            }

            "control.ack" -> {
                val acknowledgment = runCatching {
                    json.decodeFromJsonElement<ControlAckPayload>(envelope.payload)
                }.getOrElse {
                    mutableState.value = mutableState.value.copy(
                        problem = PhoneSessionProblem.ProtocolFailure,
                    )
                    return
                }
                if (mutableState.value.controls[acknowledgment.requestID] !is ControlDelivery.Pending) {
                    return
                }
                timeoutJobs.remove(acknowledgment.requestID)?.cancel()
                updateControl(
                    acknowledgment.requestID,
                    ControlDelivery.Acknowledged(
                        result = acknowledgment.result,
                        reason = acknowledgment.reason,
                    ),
                )
            }
        }
    }

    /**
     * 桥每次重试都会换 Quick Tunnel 域名。域名是运行时状态，不是配对身份的一部分：
     * 收到新 host 时更新本地保存的配对，下一次出门重连才会拨新地址。
     * 旧地址已经失联、手机又不在局域网时收不到这条快照，这种情况仍然只能重新配对。
     */
    private suspend fun applyTunnelHostLocked(reported: String?) {
        val current = pairing ?: return
        val host = normalizeTunnelHost(reported) ?: return
        if (host == normalizeTunnelHost(current.tunnelHost)) return

        val updated = current.copy(tunnelHost = host)
        pairing = updated
        refreshEndpointsLocked(updated)
        runCatching { credentialStore.save(updated) }
    }

    private fun refreshEndpointsLocked(value: PairingPayload) {
        val candidates = endpointsFor(value)
        if (candidates == endpoints) return

        // 隧道域名轮换后旧端点对象已不在候选里：按"上次走隧道"的意图落到新域名，
        // 不要回落到 index 0 的局域网（户外会在下次重连时白等一次超时）。
        val active = activeEndpointLocked(candidates)
        endpoints = candidates
        endpointIndex = active?.let { candidates.indexOf(it) }?.takeIf { it >= 0 } ?: 0
    }

    private fun connectLocked(value: PairingPayload) {
        val candidates = endpointsFor(value)
        if (candidates.isEmpty()) return
        // 候选集合因网络类型/隧道域名变化而变时，保留上一次尝试的腿（按 secure 对齐），
        // 不要无脑回落到 index 0：在外地 WiFi 上重连隧道不该先撞一次局域网。
        val index = if (endpoints == candidates) {
            endpointIndex.coerceIn(0, candidates.lastIndex)
        } else {
            val previous = connectingEndpoint ?: endpoints.getOrNull(endpointIndex)
            val preserved = previous?.let { old -> candidates.firstOrNull { it.secure == old.secure } }
            preserved?.let { candidates.indexOf(it) }?.takeIf { it >= 0 } ?: 0
        }
        startConnectLocked(candidates, index)
    }

    private fun startConnectLocked(candidates: List<PhoneEndpoint>, index: Int) {
        endpoints = candidates
        endpointIndex = index
        connectingEndpoint = candidates[index]
        mutableState.value = mutableState.value.copy(
            link = LinkState.CONNECTING,
            linkRoute = null,
        )
        transport.connect(candidates[index])
    }

    /**
     * 候选地址：本地网络 LAN 优先；蜂窝等 Remote 网络隧道优先，但保留 LAN 作为失败兜底
     * （Wi-Fi/热点仍可能连到同一台桥）。主路径不会先等 LAN，只有隧道失败才会试一次。
     */
    private fun endpointsFor(value: PairingPayload): List<PhoneEndpoint> {
        val local = PhoneEndpoint(value.host, value.port, secret = value.secret)
        val tunnel = tunnelHostOf(value)
            ?.let { PhoneEndpoint(it, 443, secure = true, secret = value.secret) }
        return when {
            tunnel == null -> listOf(local)
            networkKind() == NetworkKind.Remote -> listOf(tunnel, local)
            else -> listOf(local, tunnel)
        }
    }

    /**
     * 当前连接（或正在拨的）候选。隧道换域名后旧对象不在候选里时，按 secure 落到新隧道；
     * 上传/截图据此把当前腿排第一，也用来判断要不要切腿。
     */
    private fun activeEndpointLocked(candidates: List<PhoneEndpoint>): PhoneEndpoint? {
        val attempted = connectingEndpoint ?: endpoints.getOrNull(endpointIndex) ?: return null
        candidates.firstOrNull { it == attempted }?.let { return it }
        if (!attempted.secure) return null
        return candidates.firstOrNull { it.secure }
    }

    /**
     * 桥那边已经洗过一遍了，但配对文本可能是手改过的，这里再洗一次：
     * 粘整段 https://xxx.ngrok-free.dev/ 进来是常态，不洗会拼出废 URL。
     */
    private fun tunnelHostOf(value: PairingPayload): String? =
        normalizeTunnelHost(value.tunnelHost)

    private fun normalizeTunnelHost(raw: String?): String? {
        val value = raw?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        return value.substringAfter("://").substringBefore('/').substringBefore(':').trim()
            .takeIf { it.isNotEmpty() }
    }

    private fun disconnectAndRetryLocked(advanceEndpoint: Boolean) {
        val failed = connectingEndpoint
        if (failed != null && !failed.secure) {
            localUpgradeBlockedUntil = now() + LAN_UPGRADE_COOLDOWN_MS
        }
        connectingEndpoint = null
        mutableState.value = mutableState.value.copy(
            link = LinkState.DISCONNECTED,
            linkRoute = null,
            problem = PhoneSessionProblem.TransportFailure,
        )
        if (stopped || pairing == null || reconnectJob?.isActive == true) return
        // 隧道首次握手失败时不要盲目轮换回局域网：先探测，LAN 真的可达才切；
        // 否则在异地 WiFi 上会退回 LAN 再等一次 connect timeout。
        if (advanceEndpoint && failed?.secure == true && networkKind() != NetworkKind.Remote) {
            val value = pairing ?: return
            if (endpointsFor(value).any { !it.secure }) {
                requestTune(force = true)
                return
            }
        }
        // 连不上就换下一个候选：局域网不通试隧道，隧道不通再试回局域网。
        // 只在"根本没连上"时换 —— 断连（连上过又掉）保持原地址，否则在家待着也会漂到隧道去。
        if (advanceEndpoint && endpoints.size > 1) {
            endpointIndex = (endpointIndex + 1) % endpoints.size
        }
        val exponent = minOf(retryCount++, 5)
        val waitMillis = minOf(30_000L, 1_000L shl exponent)
        reconnectJob = scope.launch {
            wait(waitMillis)
            mutex.withLock {
                if (!stopped) {
                    pairing?.let(::connectLocked)
                }
                reconnectJob = null
            }
        }
    }

    /** 取消退避，立即重连当前候选；已连接或无配对时不动。 */
    private fun reconnectNowLocked() {
        if (stopped || pairing == null) return
        if (mutableState.value.link == LinkState.CONNECTED) return
        reconnectJob?.cancel()
        reconnectJob = null
        retryCount = 0
        transport.disconnect()
        pairing?.let(::connectLocked)
    }

    private sealed interface RoutePlan {
        data object None : RoutePlan
        data object Reconnect : RoutePlan
        data class Switch(val target: PhoneEndpoint) : RoutePlan
        data class Probe(val lan: PhoneEndpoint, val mode: ProbeMode) : RoutePlan
    }

    private enum class ProbeMode {
        /** 已连隧道、WiFi 回来：探测 LAN，通才升级。 */
        Upgrade,

        /** 正在连 LAN：探测 LAN，不通就改连隧道，不再等 connect timeout。 */
        Connecting,

        /** 当前没连接：探测后直接选 LAN 或隧道。 */
        ColdStart,
    }

    /**
     * 网络变化后的唯一调度入口。核心是**非对称切换**：
     * - 降级到隧道（蜂窝）不需要探测，立刻切；
     * - 升级到局域网必须先探测，避免异地 WiFi 把一条健康隧道掐掉。
     */
    private suspend fun tuneRoute(kind: NetworkKind, generation: Int) {
        val plan = mutex.withLock {
            if (generation != pairingGeneration) return
            planRouteLocked(kind)
        }
        when (plan) {
            RoutePlan.None -> Unit
            RoutePlan.Reconnect -> mutex.withLock {
                if (generation == pairingGeneration) reconnectNowLocked()
            }
            is RoutePlan.Switch -> mutex.withLock {
                if (generation != pairingGeneration) return@withLock
                // 计划是"蜂窝→隧道"，执行前 WiFi 刚回来：放弃这次过期切换，让下一次
                // onNetworkChanged 走探测升级，避免无谓地把刚恢复的局域网候选丢掉。
                if (plan.target.secure && networkKind() != NetworkKind.Remote) return@withLock
                val value = pairing ?: return@withLock
                val candidates = endpointsFor(value)
                val target = candidates.firstOrNull { it.secure == plan.target.secure } ?: return@withLock
                if (activeEndpointLocked(candidates) == target &&
                    mutableState.value.link == LinkState.CONNECTED
                ) {
                    return@withLock
                }
                switchToLocked(candidates, target)
            }
            is RoutePlan.Probe -> {
                if (plan.mode == ProbeMode.ColdStart) {
                    // 没连接时由这次探测决定首选腿；取消已经排队的旧候选重连，避免
                    // 探测还没回来就被退避任务按旧网络拨了局域网。
                    mutex.withLock {
                        if (generation != pairingGeneration) return@withLock
                        reconnectJob?.cancel()
                        reconnectJob = null
                    }
                }
                var retryIndex = 0
                while (true) {
                    val reachable = probeLocal(plan.lan)
                    currentCoroutineContext().ensureActive()
                    val retry = mutex.withLock {
                        if (stopped) return@withLock false
                        // 探测期间网络/配对可能已经变了：只在预期状态仍成立时动手。
                        if (generation != pairingGeneration) return@withLock false
                        if (networkKind() != kind) return@withLock false
                        val value = pairing ?: return@withLock false
                        val candidates = endpointsFor(value)
                        val local = candidates.firstOrNull { !it.secure }
                        val tunnel = candidates.firstOrNull { it.secure }
                        if (plan.lan != local) return@withLock false
                        val link = mutableState.value.link
                        when (plan.mode) {
                            ProbeMode.Upgrade -> {
                                val active = activeEndpointLocked(candidates)
                                if (reachable && link == LinkState.CONNECTED &&
                                    active?.secure == true
                                ) {
                                    switchToLocked(candidates, local)
                                    false
                                } else {
                                    !reachable && link == LinkState.CONNECTED &&
                                        active?.secure == true &&
                                        retryIndex < LAN_UPGRADE_RETRY_DELAYS_MS.size
                                }
                            }

                            ProbeMode.Connecting -> {
                                if (!reachable && tunnel != null && link == LinkState.CONNECTING &&
                                    activeEndpointLocked(candidates)?.secure == false
                                ) {
                                    // 这次探测失败也是局域网失败：冷却期内隧道 Opened 后别再补探一次。
                                    localUpgradeBlockedUntil = now() + LAN_UPGRADE_COOLDOWN_MS
                                    switchToLocked(candidates, tunnel)
                                }
                                false
                            }

                            ProbeMode.ColdStart -> {
                                if (link == LinkState.DISCONNECTED) {
                                    when {
                                        reachable -> switchToLocked(candidates, local)
                                        tunnel != null -> {
                                            localUpgradeBlockedUntil = now() + LAN_UPGRADE_COOLDOWN_MS
                                            switchToLocked(candidates, tunnel)
                                        }
                                        else -> reconnectNowLocked()
                                    }
                                }
                                false
                            }
                        }
                    }
                    if (!retry) return
                    wait(LAN_UPGRADE_RETRY_DELAYS_MS[retryIndex])
                    retryIndex += 1
                }
            }
        }
    }

    private fun planRouteLocked(kind: NetworkKind): RoutePlan {
        val value = pairing ?: return RoutePlan.None
        val candidates = endpointsFor(value)
        val local = candidates.firstOrNull { !it.secure }
        val tunnel = candidates.firstOrNull { it.secure }
        val active = activeEndpointLocked(candidates)
        val link = mutableState.value.link

        if (kind == NetworkKind.Remote) {
            // 蜂窝：局域网不可能可达。已连隧道不动；连着/正在连局域网就立刻切隧道。
            return when {
                tunnel == null -> if (link == LinkState.DISCONNECTED) RoutePlan.Reconnect else RoutePlan.None
                link == LinkState.DISCONNECTED -> RoutePlan.Reconnect
                active == tunnel -> RoutePlan.None
                else -> RoutePlan.Switch(tunnel)
            }
        }

        if (local == null) {
            return if (link == LinkState.DISCONNECTED) RoutePlan.Reconnect else RoutePlan.None
        }
        return when (link) {
            LinkState.DISCONNECTED -> RoutePlan.Probe(local, ProbeMode.ColdStart)
            LinkState.CONNECTING ->
                if (active == local) RoutePlan.Probe(local, ProbeMode.Connecting) else RoutePlan.None

            LinkState.CONNECTED ->
                if (active == local) RoutePlan.None else RoutePlan.Probe(local, ProbeMode.Upgrade)
        }
    }

    private fun switchToLocked(candidates: List<PhoneEndpoint>, target: PhoneEndpoint) {
        val index = candidates.indexOf(target)
        if (index < 0) return
        reconnectJob?.cancel()
        reconnectJob = null
        retryCount = 0
        transport.disconnect()
        startConnectLocked(candidates, index)
    }

    private fun updateControl(requestID: String, delivery: ControlDelivery) {
        val controls = LinkedHashMap(mutableState.value.controls)
        controls[requestID] = delivery
        while (controls.size > MAX_CONTROL_HISTORY) {
            controls.remove(controls.keys.first())
        }
        mutableState.value = mutableState.value.copy(controls = controls)
    }

    private fun validatePairing(value: PairingPayload): PhoneSessionProblem? {
        if (value.version != 1) return PhoneSessionProblem.UnsupportedVersion
        if (value.host.isBlank() || value.port !in 1..65_535) {
            return PhoneSessionProblem.InvalidEndpoint
        }
        val validSecret = value.secret.isNotBlank() && runCatching {
            Base64.getDecoder().decode(value.secret)
        }.getOrNull()?.isNotEmpty() == true
        if (!validSecret) return PhoneSessionProblem.InvalidSecret
        return null
    }

    private val PairingPayload.summary: PairingSummary
        get() = PairingSummary(serviceID, host, port)

    @Serializable
    private data class IncomingEnvelope(
        val version: Int,
        val messageId: String,
        val type: String,
        val sentAt: Long,
        val payload: JsonElement,
    )

    private companion object {
        const val MAX_CONTROL_HISTORY = 50
        const val PROBE_TIMEOUT_MS = 1_200
        const val LAN_UPGRADE_COOLDOWN_MS = 20_000L
        private val LAN_UPGRADE_RETRY_DELAYS_MS = longArrayOf(600L, 1_500L, 3_000L)
    }
}
