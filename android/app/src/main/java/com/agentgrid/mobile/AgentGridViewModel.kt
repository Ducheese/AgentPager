package com.agentgrid.mobile

import android.app.Application
import android.content.Intent
import androidx.compose.runtime.mutableStateMapOf
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.agentgrid.mobile.domain.ControlAction
import com.agentgrid.mobile.domain.PendingRequest
import com.agentgrid.mobile.domain.StateSnapshotPayload
import com.agentgrid.mobile.domain.TaskDashboardProjection
import com.agentgrid.mobile.domain.TaskDashboardProjector
import com.agentgrid.mobile.domain.TaskSnapshot
import com.agentgrid.mobile.domain.UsageSnapshot
import com.agentgrid.mobile.network.LinkState
import com.agentgrid.mobile.network.PairingOutcome
import com.agentgrid.mobile.network.PairingSummary
import com.agentgrid.mobile.network.PhoneSessionFactory
import com.agentgrid.mobile.network.PhoneSessionProblem
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
) {
    val focusedTask: TaskSnapshot?
        get() = taskProjection.focusedTask
}

class AgentGridViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences =
        application.getSharedPreferences("agentgrid-settings", Application.MODE_PRIVATE)
    private val mutableState = MutableStateFlow(
        AgentGridUiState(
            terminalMode = preferences.getBoolean("terminal-mode", true),
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

    private val phoneSession = PhoneSessionFactory.shared(application)
    private var keepAliveRequested = false
    private var latestSnapshot = StateSnapshotPayload(emptyList())
    private var dashboardTransitionJob: Job? = null

    init {
        viewModelScope.launch {
            phoneSession.state.collect { session ->
                val snapshotChanged = latestSnapshot != session.snapshot
                latestSnapshot = session.snapshot
                val projection = if (snapshotChanged) {
                    TaskDashboardProjector.project(
                        snapshot = session.snapshot,
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
        viewModelScope.launch {
            phoneSession.control(taskID, action, value, values)
        }
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

    override fun onCleared() {
        dashboardTransitionJob?.cancel()
        // 会话是进程级单例：Activity/ViewModel 销毁不等于断开，保活服务还握着这条连接。
        // 真正断开只有 unpair（或进程死亡）。
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
