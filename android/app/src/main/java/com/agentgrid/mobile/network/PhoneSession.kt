package com.agentgrid.mobile.network

import com.agentgrid.mobile.domain.ControlAckPayload
import com.agentgrid.mobile.domain.ControlAction
import com.agentgrid.mobile.domain.ControlPayload
import com.agentgrid.mobile.domain.ControlResult
import com.agentgrid.mobile.domain.PairingPayload
import com.agentgrid.mobile.domain.SignedControlEnvelope
import com.agentgrid.mobile.domain.StateSnapshotPayload
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
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
     * 附件上传用的候选地址（含配对密钥）：与 WS 连接走同一套局域网优先+隧道兜底。
     * 未配对时返回空列表。
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
)

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
    private var stopped = false
    private var retryCount = 0
    private var reconnectJob: Job? = null
    /** 候选地址：局域网在前、隧道在后。连不上就轮换到下一个（见 disconnectAndRetryLocked）。 */
    private var endpoints: List<PhoneEndpoint> = emptyList()
    private var endpointIndex = 0
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
            transport.disconnect()
            pairing = decoded
            retryCount = 0
            // 重新配对等于重新开始：候选从头走（能扫到码就说明人在局域网里）
            endpoints = emptyList()
            endpointIndex = 0
            mutableState.value = PhoneSessionState(
                pairing = decoded.summary,
                link = LinkState.CONNECTING,
            )
            connectLocked(decoded)
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
            mutex.withLock {
                if (stopped || pairing == null) return@withLock
                // 健康连接不折腾；重连在飞（CONNECTING）或被冻结卡住时，断开重来一次最快。
                if (mutableState.value.link == LinkState.CONNECTED) return@withLock
                reconnectJob?.cancel()
                reconnectJob = null
                retryCount = 0
                transport.disconnect()
                pairing?.let(::connectLocked)
            }
        }
    }

    override fun close() {
        stopped = true
        reconnectJob?.cancel()
        timeoutJobs.values.forEach(Job::cancel)
        transport.disconnect()
        transport.close()
        scope.cancel()
    }

    override suspend fun uploadEndpoints(): List<PhoneEndpoint> = mutex.withLock {
        val current = pairing ?: return emptyList()
        endpointsFor(current)
    }

    private suspend fun acceptTransportEvent(event: TransportEvent) {
        mutex.withLock {
            when (event) {
                TransportEvent.Opened -> {
                    retryCount = 0
                    reconnectJob?.cancel()
                    reconnectJob = null
                    mutableState.value = mutableState.value.copy(
                        link = LinkState.CONNECTED,
                        problem = null,
                    )
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

    private fun acceptMessageLocked(text: String) {
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

    private fun connectLocked(value: PairingPayload) {
        val candidates = endpointsFor(value)
        // 配对没变时保留轮换位置（重连要接着上次的候选走，不能每次都退回局域网）。
        if (endpoints != candidates) {
            endpoints = candidates
            endpointIndex = 0
        }
        mutableState.value = mutableState.value.copy(link = LinkState.CONNECTING)
        transport.connect(endpoints[endpointIndex])
    }

    /**
     * 候选地址：局域网优先，配了隧道域名就再挂一个 wss://域名:443。
     * 在家连局域网、出门自动落到隧道，不需要人手动切。
     */
    private fun endpointsFor(value: PairingPayload): List<PhoneEndpoint> {
        val local = PhoneEndpoint(value.host, value.port, secret = value.secret)
        val tunnel = tunnelHostOf(value)
            ?.let { PhoneEndpoint(it, 443, secure = true, secret = value.secret) }
        return if (tunnel == null) listOf(local) else listOf(local, tunnel)
    }

    /**
     * 桥那边已经洗过一遍了，但配对文本可能是手改过的，这里再洗一次：
     * 粘整段 https://xxx.ngrok-free.dev/ 进来是常态，不洗会拼出废 URL。
     */
    private fun tunnelHostOf(value: PairingPayload): String? {
        val raw = value.tunnelHost?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        val host = raw.substringAfter("://").substringBefore('/').substringBefore(':').trim()
        return host.takeIf { it.isNotEmpty() }
    }

    private fun disconnectAndRetryLocked(advanceEndpoint: Boolean) {
        mutableState.value = mutableState.value.copy(
            link = LinkState.DISCONNECTED,
            problem = PhoneSessionProblem.TransportFailure,
        )
        if (stopped || pairing == null || reconnectJob?.isActive == true) return
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
    }
}
