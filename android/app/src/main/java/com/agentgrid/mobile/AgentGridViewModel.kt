package com.agentgrid.mobile

import android.app.Application
import android.content.Intent
import androidx.compose.runtime.mutableStateMapOf
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import android.net.Uri
import com.agentgrid.mobile.attachments.AttachmentLimits
import com.agentgrid.mobile.attachments.AttachmentState
import com.agentgrid.mobile.attachments.AttachmentUploader
import com.agentgrid.mobile.attachments.ImageCompressor
import com.agentgrid.mobile.attachments.PromptAssembler
import com.agentgrid.mobile.attachments.PromptAttachment
import com.agentgrid.mobile.attachments.PromptTokens
import com.agentgrid.mobile.domain.ControlAction
import com.agentgrid.mobile.domain.ControlResult
import com.agentgrid.mobile.domain.PendingRequest
import com.agentgrid.mobile.domain.StateSnapshotPayload
import com.agentgrid.mobile.domain.TaskDashboardProjection
import com.agentgrid.mobile.domain.TaskDashboardProjector
import com.agentgrid.mobile.domain.TaskSnapshot
import com.agentgrid.mobile.domain.UsageSnapshot
import com.agentgrid.mobile.network.ControlDelivery
import com.agentgrid.mobile.network.ControlSubmission
import com.agentgrid.mobile.network.LinkState
import com.agentgrid.mobile.network.PairingOutcome
import com.agentgrid.mobile.network.PairingSummary
import com.agentgrid.mobile.network.PhoneSessionFactory
import com.agentgrid.mobile.network.PhoneSessionProblem
import com.agentgrid.mobile.network.PhoneSessionState
import com.agentgrid.mobile.service.LinkKeepAliveService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

data class AgentGridUiState(
    val pairing: PairingSummary? = null,
    val linkState: LinkState = LinkState.DISCONNECTED,
    val taskProjection: TaskDashboardProjection = TaskDashboardProjection.empty(),
    val usage: UsageSnapshot? = null,
    val usages: List<UsageSnapshot> = emptyList(),
    val pendingRequests: List<PendingRequest> = emptyList(),
    val pairingError: String? = null,
    val terminalMode: Boolean = true,
    val speakingTaskId: String? = null,
    val controlFeedback: Map<String, ControlFeedback> = emptyMap(),
    /** 从完成通知点进来、还没消费的任务；等它出现在快照里再展开/滚动。 */
    val openTaskRequest: String? = null,
    val speechFailure: ReplySpeech.SpeechFailure? = null,
    val speechRate: Float = 2.0f,
) {
    val focusedTask: TaskSnapshot?
        get() = taskProjection.focusedTask
}

enum class ControlFeedbackKind { SENDING, SENT, FAILED }

data class ControlFeedback(
    val action: ControlAction,
    val kind: ControlFeedbackKind,
    val message: String? = null,
)

class AgentGridViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences =
        application.getSharedPreferences("agentgrid-settings", Application.MODE_PRIVATE)
    private val mutableState = MutableStateFlow(
        AgentGridUiState(
            terminalMode = preferences.getBoolean("terminal-mode", true),
            speechRate = preferences.getFloat("speech-rate", 2.0f),
        ),
    )
    val state: StateFlow<AgentGridUiState> = mutableState.asStateFlow()

    /**
     * prompt 输入草稿：按 taskID 保存，切换 task/收纳面板都不丢，只有发送成功才清空。
     * 放在 ViewModel（Activity 级）而不是 Composable 的 remember —— remember 的生命周期绑
     * 收纳动画，收起再展开草稿就没了。
     */
    val promptDrafts = mutableStateMapOf<String, String>()

    fun setPromptDraft(taskId: String, text: String) {
        if (text.isEmpty()) promptDrafts.remove(taskId) else promptDrafts[taskId] = text
    }

    /**
     * 附图：按 taskID 保存选中列表，发送成功才清空。附件走独立上传通道（multipart 直传
     * Bridge 暂存区），prompt 文本发送时在 App 端把 READY 附件拼成文末清单——控制通道
     * 的签名/重放协议零改动。
     */
    val promptAttachments = mutableStateMapOf<String, List<PromptAttachment>>()
    private var attachmentSeq = 0
    /** 每张图一条上传协程：删图时取消，免得传完在 PC 上留下没人引用的文件。 */
    private val attachmentJobs = mutableMapOf<String, Job>()

    private val attachmentUploader: AttachmentUploader by lazy {
        // 密钥与候选地址与 WS 共用同一套（含 wss 隧道兜底），见 DefaultPhoneSession.uploadEndpoints。
        // 单例：OkHttpClient 自带连接池，一批 5~10 张图复用同一条连接。
        AttachmentUploader(endpoints = { phoneSession.uploadEndpoints() })
    }

    /** 选图入口：压缩→后台上传→READY。失败保留 FAILED 卡片可重试，不阻塞打字。 */
    fun addAttachments(taskId: String, uris: List<Uri>) {
        val current = promptAttachments[taskId].orEmpty()
        val room = AttachmentLimits.MAX_PER_PROMPT - current.size
        if (room <= 0) {
            // API < 33 的相册回退不保证遵守 maxItems，超出的必须明说，不能悄悄丢。
            setFeedback(
                taskId,
                ControlFeedback(
                    ControlAction.PROMPT,
                    ControlFeedbackKind.FAILED,
                    "最多 ${AttachmentLimits.MAX_PER_PROMPT} 张，先删一张再加",
                ),
            )
            return
        }
        val accepted = uris.take(room)
        if (accepted.isEmpty()) return
        if (accepted.size < uris.size) {
            setFeedback(
                taskId,
                ControlFeedback(
                    ControlAction.PROMPT,
                    ControlFeedbackKind.FAILED,
                    "最多 ${AttachmentLimits.MAX_PER_PROMPT} 张，本次忽略了 ${uris.size - accepted.size} 张",
                ),
            )
        }
        val seeded = accepted.map { uri ->
            attachmentSeq += 1
            PromptAttachment("att-$attachmentSeq", "shot-$attachmentSeq.jpg", sourceUri = uri)
        }
        promptAttachments[taskId] = current + seeded
        seeded.forEachIndexed { index, attachment ->
            val uri = accepted[index]
            val job = viewModelScope.launch {
                updateAttachment(taskId, attachment.clientId) { it.copy(state = AttachmentState.COMPRESSING) }
                val image = runCatching { ImageCompressor.compress(getApplication(), uri) }
                    .getOrElse { error ->
                        updateAttachment(taskId, attachment.clientId) {
                            it.copy(state = AttachmentState.FAILED, error = error.message ?: "压缩失败")
                        }
                        return@launch
                    }
                updateAttachment(taskId, attachment.clientId) {
                    it.copy(
                        jpegBytes = image.bytes,
                        meta = image.meta,
                        thumb = image.thumb,
                        state = AttachmentState.UPLOADING,
                    )
                }
                runCatching { attachmentUploader.upload(image.bytes, attachment.fileName, image.meta) }
                    .onSuccess { response ->
                        updateAttachment(taskId, attachment.clientId) {
                            // 大图字节释放，缩略图留着给卡片显示。
                            it.copy(jpegBytes = null, state = AttachmentState.READY, localPath = response.localPath, error = null)
                        }
                    }
                    .onFailure { error ->
                        updateAttachment(taskId, attachment.clientId) {
                            it.copy(state = AttachmentState.FAILED, error = error.message ?: "上传失败")
                        }
                    }
            }
            trackAttachmentJob(taskId, attachment.clientId, job)
        }
    }

    /** 删图即重排：占位符跟着删，后面编号整体减一，保证卡号与 [图N] 永远对得上。 */
    fun removeAttachment(taskId: String, clientId: String) {
        val current = promptAttachments[taskId].orEmpty()
        val index = current.indexOfFirst { it.clientId == clientId }
        if (index < 0) return
        cancelAttachmentJob(taskId, clientId)
        promptAttachments[taskId] = current.filterNot { it.clientId == clientId }
        val draft = promptDrafts[taskId].orEmpty()
        if (draft.isNotEmpty()) {
            val renumbered = PromptTokens.renumberAfterRemoval(draft, index + 1)
            if (renumbered != draft) setPromptDraft(taskId, renumbered)
        }
    }

    fun retryAttachment(taskId: String, clientId: String) {
        val attachment = promptAttachments[taskId].orEmpty().firstOrNull { it.clientId == clientId } ?: return
        val job = viewModelScope.launch {
            val existingBytes = attachment.jpegBytes
            val bytes: ByteArray
            val meta: String?
            var thumb = attachment.thumb
            if (existingBytes != null) {
                bytes = existingBytes
                meta = attachment.meta
            } else {
                // 上次死在压缩/超限，手里没有字节，从相册 URI 重压一遍。
                val uri = attachment.sourceUri ?: return@launch
                updateAttachment(taskId, clientId) {
                    it.copy(state = AttachmentState.COMPRESSING, error = null)
                }
                val image = runCatching { ImageCompressor.compress(getApplication(), uri) }
                    .getOrElse { error ->
                        updateAttachment(taskId, clientId) {
                            it.copy(state = AttachmentState.FAILED, error = error.message ?: "压缩失败")
                        }
                        return@launch
                    }
                bytes = image.bytes
                meta = image.meta
                thumb = image.thumb
            }
            updateAttachment(taskId, clientId) {
                it.copy(jpegBytes = bytes, meta = meta, thumb = thumb, state = AttachmentState.UPLOADING, error = null)
            }
            runCatching { attachmentUploader.upload(bytes, attachment.fileName, meta) }
                .onSuccess { response ->
                    updateAttachment(taskId, clientId) {
                        it.copy(jpegBytes = null, state = AttachmentState.READY, localPath = response.localPath, error = null)
                    }
                }
                .onFailure { error ->
                    updateAttachment(taskId, clientId) {
                        it.copy(state = AttachmentState.FAILED, error = error.message ?: "上传失败")
                    }
                }
        }
        trackAttachmentJob(taskId, clientId, job)
    }

    private fun trackAttachmentJob(taskId: String, clientId: String, job: Job) {
        val key = "$taskId|$clientId"
        attachmentJobs[key] = job
        job.invokeOnCompletion {
            if (attachmentJobs[key] === job) attachmentJobs.remove(key)
        }
    }

    private fun cancelAttachmentJob(taskId: String, clientId: String) {
        attachmentJobs.remove("$taskId|$clientId")?.cancel()
    }

    private fun updateAttachment(taskId: String, clientId: String, transform: (PromptAttachment) -> PromptAttachment) {
        // key 已被删掉时不再写回：否则 map 会被重新插入一个空 list。
        val current = promptAttachments[taskId] ?: return
        promptAttachments[taskId] = current.map { item ->
            if (item.clientId == clientId) transform(item) else item
        }
    }

    private fun clearAttachments(taskId: String) {
        promptAttachments.remove(taskId)
    }

    /** 与桥端 TaskCatalog.Normalize 对齐：连续空白折成一个空格并去首尾。 */
    private fun normalizePromptText(text: String): String = text.replace(Regex("\\s+"), " ").trim()

    private val phoneSession = PhoneSessionFactory.shared(application)
    private var keepAliveRequested = false
    private var latestSnapshot = StateSnapshotPayload(emptyList())
    private var dashboardTransitionJob: Job? = null
    private var openTaskTimeout: Job? = null

    private data class PendingControl(
        val taskID: String,
        val action: ControlAction,
        val sentValue: String?,
        /** PROMPT 带附件时 sentValue 是组装后的终态，rawValue 才是输入框原文（用于草稿比对）。 */
        val rawValue: String? = null,
    )

    private val pendingControls = mutableMapOf<String, PendingControl>()
    private val feedbackJobs = mutableMapOf<String, Job>()

    /** 只有这些动作值得占用一行反馈；mark-read/mute 之类静默发送，不打扰界面。 */
    private val trackedActions = setOf(
        ControlAction.PROMPT,
        ControlAction.ANSWER,
        ControlAction.APPROVE,
        ControlAction.DENY,
    )

    init {
        viewModelScope.launch {
            phoneSession.state.collect { session ->
                val snapshotChanged = latestSnapshot != session.snapshot
                latestSnapshot = session.snapshot
                val requestedOpen = mutableState.value.openTaskRequest
                    ?.takeIf { id -> session.snapshot.tasks.any { it.id == id } }
                val projection = if (snapshotChanged) {
                    TaskDashboardProjector.project(
                        tasks = session.snapshot.tasks,
                        preferredFocusedTaskID = requestedOpen ?: session.snapshot.focusedTaskID,
                        previous = mutableState.value.taskProjection,
                        now = System.currentTimeMillis(),
                    )
                } else {
                    mutableState.value.taskProjection
                }
                val allUsages = session.snapshot.usages.ifEmpty {
                    listOfNotNull(session.snapshot.usage)
                }
                mutableState.value = mutableState.value.copy(
                    pairing = session.pairing,
                    linkState = session.link,
                    taskProjection = projection,
                    usage = session.snapshot.usage,
                    usages = allUsages,
                    pendingRequests = session.snapshot.pendingRequests,
                )
                if (session.pairing != null) ensureKeepAliveService()
                if (snapshotChanged) scheduleDashboardTransition(projection)
                resolveControls(session)
            }
        }
        viewModelScope.launch {
            phoneSession.start()
        }
        viewModelScope.launch {
            ReplySpeech.speakingTaskId.collect { speakingTaskId ->
                mutableState.value = mutableState.value.copy(speakingTaskId = speakingTaskId)
            }
        }
        viewModelScope.launch {
            ReplySpeech.failure.collect { failure ->
                mutableState.value = mutableState.value.copy(speechFailure = failure)
            }
        }
    }

    /** 回前台时立刻把退避里的重连拎起来（配合保活服务，后台掉线只丢几秒）。 */
    fun onForeground() {
        phoneSession.nudge()
        if (mutableState.value.pairing != null) ensureKeepAliveService()
    }

    private fun ensureKeepAliveService() {
        if (keepAliveRequested) return
        keepAliveRequested = true
        runCatching {
            ContextCompat.startForegroundService(
                getApplication(),
                Intent(getApplication(), LinkKeepAliveService::class.java),
            )
        }.onFailure { keepAliveRequested = false }
    }

    fun pair(text: String) {
        viewModelScope.launch {
            when (val outcome = phoneSession.pair(text)) {
                PairingOutcome.Connecting -> {
                    mutableState.value = mutableState.value.copy(pairingError = null)
                }

                is PairingOutcome.Rejected -> {
                    mutableState.value = mutableState.value.copy(
                        pairingError = pairingError(outcome.problem),
                    )
                }
            }
        }
    }

    /**
     * 点 REPLY 标签：朗读/停止。
     * 仅完成态（SUCCEEDED / INTERRUPTED）允许开读；开读后即使任务又回到运行态，再点仍然能停。
     */
    fun toggleReplySpeech(taskId: String) {
        if (ReplySpeech.speakingTaskId.value == taskId) {
            ReplySpeech.stop()
            return
        }
        val task = mutableState.value.taskProjection.orderedTasks
            .firstOrNull { it.id == taskId }
            ?: return
        if (!task.isTerminal) return
        val body = task.responseBody ?: return
        ReplySpeech.speak(getApplication(), taskId, body)
    }

    fun unpair() {
        viewModelScope.launch {
            ReplySpeech.stop()
            phoneSession.unpair()
            keepAliveRequested = false
            pendingControls.clear()
            feedbackJobs.values.forEach { it.cancel() }
            feedbackJobs.clear()
            promptDrafts.clear()
            mutableState.value = mutableState.value.copy(controlFeedback = emptyMap())
            runCatching {
                getApplication<Application>().stopService(
                    Intent(getApplication(), LinkKeepAliveService::class.java),
                )
            }
        }
    }

    fun control(
        taskID: String,
        action: ControlAction,
        value: String? = null,
        values: List<String>? = null,
    ) {
        if (action !in trackedActions) {
            viewModelScope.launch { phoneSession.control(taskID, action, value, values) }
            return
        }
        // Prompt 发送：App 端把 READY 附件拼成文末清单，控制通道原文透传。
        // 非 READY（压缩/上传中、失败）一律拦下：否则清单序号会与图片条错位，
        // 失败那张还会在发送成功后被静默清掉。
        if (action == ControlAction.PROMPT &&
            promptAttachments[taskID].orEmpty().any { it.state != AttachmentState.READY }
        ) {
            setFeedback(
                taskID,
                ControlFeedback(action, ControlFeedbackKind.FAILED, "还有图片未就绪，稍后再发"),
            )
            return
        }
        val effectiveValue = if (action == ControlAction.PROMPT) {
            PromptAssembler.assemble(value.orEmpty(), promptAttachments[taskID].orEmpty())
        } else {
            value
        }
        if (action == ControlAction.PROMPT && effectiveValue.isNullOrBlank()) return
        // 同一个任务同一个动作还没有结果时不再发第二遍（按钮虽已禁用，这里兜住其它入口）
        if (pendingControls.values.any { it.taskID == taskID && it.action == action }) return
        setFeedback(taskID, ControlFeedback(action, ControlFeedbackKind.SENDING))
        viewModelScope.launch {
            val submission = runCatching { phoneSession.control(taskID, action, effectiveValue, values) }
                .getOrElse { ControlSubmission.Rejected(PhoneSessionProblem.TransportFailure) }
            when (submission) {
                is ControlSubmission.Rejected -> setFeedback(
                    taskID,
                    ControlFeedback(action, ControlFeedbackKind.FAILED, submission.problem.label()),
                )
                is ControlSubmission.Queued -> {
                    pendingControls[submission.requestID] = PendingControl(taskID, action, effectiveValue, value)
                    // 回执可能比 control() 返回还快，立刻对一次当前状态，避免漏判
                    resolveControls(phoneSession.state.value)
                }
            }
        }
    }

    /**
     * 把 control.ack 与快照变化归并成一次「发送中 → 已发送/失败」的判定。
     * 旧版桥只在失败时回执，所以成功还要能从快照反推（prompt 已落到任务、提问卡已消失）。
     */
    private fun resolveControls(session: PhoneSessionState) {
        if (pendingControls.isEmpty()) return
        val tasks = session.snapshot.tasks.associateBy { it.id }
        val waitingTaskIDs = session.snapshot.pendingRequests.mapTo(HashSet()) { it.taskID }
        val finished = mutableListOf<String>()

        for ((requestID, control) in pendingControls) {
            when (val delivery = session.controls[requestID]) {
                is ControlDelivery.Acknowledged -> {
                    if (delivery.result == ControlResult.ACCEPTED) {
                        succeed(control)
                    } else {
                        fail(control, delivery.reason ?: "服务器拒绝了这次操作")
                    }
                    finished += requestID
                }
                ControlDelivery.TimedOut -> {
                    fail(control, "发送超时，未确认送达")
                    finished += requestID
                }
                ControlDelivery.SendFailed -> {
                    fail(control, "网络发送失败")
                    finished += requestID
                }
                is ControlDelivery.Pending, null -> Unit
            }
            if (requestID in finished) continue

            val reflected = when (control.action) {
                ControlAction.PROMPT ->
                    // 桥端会把换行/连续空白折成单个空格再回显，比对前必须做同样的归一，
                    // 否则带附图的 prompt 永远等不到这条兜底回执。
                    control.sentValue != null &&
                        tasks[control.taskID]?.userPrompt?.let(::normalizePromptText) ==
                        normalizePromptText(control.sentValue)
                ControlAction.ANSWER, ControlAction.APPROVE, ControlAction.DENY ->
                    control.taskID !in waitingTaskIDs
                else -> false
            }
            if (reflected) {
                succeed(control)
                finished += requestID
            }
        }
        finished.forEach { pendingControls.remove(it) }
    }

    private fun succeed(control: PendingControl) {
        setFeedback(control.taskID, ControlFeedback(control.action, ControlFeedbackKind.SENT))
        // 发送期间用户没有继续改，才清掉这条草稿；新版内容保留。
        // 带附件时比对输入框原文（rawValue），组装后的终态（含文末清单）必然≠草稿。
        val draftMatches = if (control.action == ControlAction.PROMPT && control.rawValue != null) {
            promptDrafts[control.taskID] == control.rawValue
        } else {
            control.action == ControlAction.PROMPT &&
                control.sentValue != null &&
                promptDrafts[control.taskID] == control.sentValue
        }
        if (draftMatches) {
            promptDrafts.remove(control.taskID)
            clearAttachments(control.taskID)
        }
        feedbackJobs.remove(control.taskID)?.cancel()
        feedbackJobs[control.taskID] = viewModelScope.launch {
            delay(2_500)
            if (mutableState.value.controlFeedback[control.taskID]?.kind == ControlFeedbackKind.SENT) {
                setFeedback(control.taskID, null)
            }
        }
    }

    private fun fail(control: PendingControl, message: String) {
        setFeedback(
            control.taskID,
            ControlFeedback(control.action, ControlFeedbackKind.FAILED, message),
        )
    }

    private fun setFeedback(taskID: String, feedback: ControlFeedback?) {
        val current = mutableState.value.controlFeedback
        val next = if (feedback == null) current - taskID else current + (taskID to feedback)
        if (next != current) mutableState.value = mutableState.value.copy(controlFeedback = next)
    }

    private fun PhoneSessionProblem.label(): String = when (this) {
        PhoneSessionProblem.NotPaired -> "未配对"
        PhoneSessionProblem.NotConnected -> "连接已断开"
        PhoneSessionProblem.TransportFailure -> "网络发送失败"
        PhoneSessionProblem.SequenceFailure -> "发送序号异常"
        PhoneSessionProblem.ProtocolFailure -> "消息编码失败"
        PhoneSessionProblem.InvalidSecret -> "配对密钥无效"
        else -> "发送失败"
    }

    fun focus(taskID: String) {
        val projection = mutableState.value.taskProjection.focusing(taskID)
        mutableState.value = mutableState.value.copy(taskProjection = projection)
    }

    fun toggleDashboard() {
        dashboardTransitionJob?.cancel()
        dashboardTransitionJob = null
        val projection = mutableState.value.taskProjection.togglingDashboard()
        mutableState.value = mutableState.value.copy(taskProjection = projection)
    }

    fun setTerminalMode(enabled: Boolean) {
        preferences.edit { putBoolean("terminal-mode", enabled) }
        mutableState.value = mutableState.value.copy(terminalMode = enabled)
    }

    /**
     * 从完成通知进入：把该任务拉进可见集合（可能已跌出 5 条上限）、关掉额度面板、
     * 标记已读，并留下一次「展开 + 滚到这行」的请求交给界面消费。
     */
    fun openTask(taskID: String) {
        val current = mutableState.value
        val projection = TaskDashboardProjector.project(
            tasks = latestSnapshot.tasks,
            preferredFocusedTaskID = taskID,
            previous = current.taskProjection,
            manualDashboardOverride = false,
            now = System.currentTimeMillis(),
        )
        mutableState.value = current.copy(
            taskProjection = projection,
            openTaskRequest = taskID,
        )
        control(taskID, ControlAction.MARK_READ)
        // 任务若一直没出现在快照里（被删除/桥重启前的事件），别让这个请求永久挂着
        openTaskTimeout?.cancel()
        openTaskTimeout = viewModelScope.launch {
            delay(60_000)
            consumeOpenTask(taskID)
        }
    }

    fun consumeOpenTask(taskID: String) {
        openTaskTimeout?.cancel()
        openTaskTimeout = null
        if (mutableState.value.openTaskRequest == taskID) {
            mutableState.value = mutableState.value.copy(openTaskRequest = null)
        }
    }

    fun setSpeechRate(rate: Float) {
        preferences.edit { putFloat("speech-rate", rate) }
        mutableState.value = mutableState.value.copy(speechRate = rate)
    }

    override fun onCleared() {
        dashboardTransitionJob?.cancel()
        openTaskTimeout?.cancel()
        feedbackJobs.values.forEach { it.cancel() }
        feedbackJobs.clear()
        // 会话是进程级单例：Activity/ViewModel 销毁不等于断开，保活服务还握着这条连接
        // 真正断开只有 unpair（或进程死亡）
    }

    private fun scheduleDashboardTransition(projection: TaskDashboardProjection) {
        dashboardTransitionJob?.cancel()
        dashboardTransitionJob = null
        if (projection.manualDashboardOverride != null) return
        val transitionAt = projection.nextDashboardAt ?: return
        dashboardTransitionJob = viewModelScope.launch {
            delay((transitionAt - System.currentTimeMillis()).coerceAtLeast(0L))
            val refreshed = TaskDashboardProjector.project(
                snapshot = latestSnapshot,
                previous = mutableState.value.taskProjection,
                now = System.currentTimeMillis(),
            )
            mutableState.value = mutableState.value.copy(taskProjection = refreshed)
            scheduleDashboardTransition(refreshed)
        }
    }

    private fun pairingError(problem: PhoneSessionProblem): String = when (problem) {
        PhoneSessionProblem.InvalidPairing -> "二维码内容不是有效的 AgentPager 配对信息"
        PhoneSessionProblem.UnsupportedVersion -> "配对信息版本不受支持"
        PhoneSessionProblem.InvalidEndpoint -> "配对地址或端口无效"
        PhoneSessionProblem.InvalidSecret -> "配对密钥无效"
        PhoneSessionProblem.CredentialFailure -> "无法安全保存配对信息"
        else -> "无法建立 AgentPager 配对"
    }
}
