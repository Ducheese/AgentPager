package com.agentgrid.mobile

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Base64
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.core.content.ContextCompat
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.agentgrid.mobile.service.LinkKeepAliveService
import com.agentgrid.mobile.ui.AgentGridScreen

import android.content.res.Configuration

class MainActivity : ComponentActivity() {
    private val viewModel: AgentGridViewModel by viewModels()

    // 分屏/自由小窗状态：扫码界面据此决定是否渲染相机（多窗口下相机被系统封锁）
    private val multiWindowState = androidx.compose.runtime.mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Android 15+（targetSdk 35+）强制 edge-to-edge，decorFits=true 已失效；
        // 统一成 edge-to-edge，由界面用 safeDrawing inset 自己让出状态栏/刘海。
        WindowCompat.setDecorFitsSystemWindows(window, false)
        multiWindowState.value = isInMultiWindowMode
        updateSystemBars()
        handleIntent(intent)

        setContent {
            val state by viewModel.state.collectAsStateWithLifecycle()
            val notificationPermission = rememberLauncherForActivityResult(
                ActivityResultContracts.RequestPermission(),
            ) {}
            // 保活服务依赖常驻通知提醒用户在后台挂着连接；Android 13+ 要先拿到通知权限。
            // 拒绝了也不影响服务本身，只是通知不显示。
            LaunchedEffect(state.pairing != null) {
                if (state.pairing != null &&
                    Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                    ContextCompat.checkSelfPermission(
                        this@MainActivity,
                        Manifest.permission.POST_NOTIFICATIONS,
                    ) != PackageManager.PERMISSION_GRANTED
                ) {
                    notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                }
            }
            AgentGridScreen(
                state = state,
                onPair = viewModel::pair,
                onUnpair = viewModel::unpair,
                onControl = viewModel::control,
                onFocus = viewModel::focus,
                onToggleDashboard = viewModel::toggleDashboard,
                onToggleTerminalMode = {
                    viewModel.setTerminalMode(!viewModel.state.value.terminalMode)
                    updateSystemBars()
                },
                onOpenTaskConsumed = viewModel::consumeOpenTask,
                onSpeechRateChange = viewModel::setSpeechRate,
                onToggleReplySpeech = viewModel::toggleReplySpeech,
                inMultiWindow = multiWindowState.value,
                promptDrafts = viewModel.promptDrafts,
                onPromptDraftChange = viewModel::setPromptDraft,
                promptAttachments = viewModel.promptAttachments,
                onAddAttachments = viewModel::addAttachments,
                onRemoveAttachment = viewModel::removeAttachment,
                onRetryAttachment = viewModel::retryAttachment,
            )
        }
    }

    override fun onStart() {
        super.onStart()
        // 从后台回来：把被冻结期间断掉/退避里的连接立刻拎起来
        viewModel.onForeground()
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIntent(intent)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            updateSystemBars()
        }
    }

    override fun onMultiWindowModeChanged(isInMultiWindowMode: Boolean, newConfig: Configuration) {
        super.onMultiWindowModeChanged(isInMultiWindowMode, newConfig)
        multiWindowState.value = isInMultiWindowMode
        updateSystemBars()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        multiWindowState.value = isInMultiWindowMode
        updateSystemBars()
    }

    private fun updateSystemBars() {
        val dedicated = viewModel.state.value.terminalMode
        // 常亮严格跟随专用模式开关：分屏/竖屏也生效，关闭时立刻恢复系统息屏策略
        if (dedicated) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
        val isLandscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        // 分屏、小窗或竖屏模式下退出全屏沉浸，让系统手柄与系统栏正常可用；横屏专用模式保持沉浸
        if (dedicated && !isInMultiWindowMode && isLandscape) {
            enterTerminalMode()
        } else {
            exitTerminalMode()
        }
    }

    private fun enterTerminalMode() {
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    private fun exitTerminalMode() {
        WindowInsetsControllerCompat(window, window.decorView)
            .show(WindowInsetsCompat.Type.systemBars())
    }

    private fun handleIntent(intent: android.content.Intent) {
        importPairing(intent)
        // 完成通知点击：定位到具体任务并吃掉这条提醒（summary 留给自己/下一次刷新）
        intent.getStringExtra(EXTRA_TASK_ID)?.let { taskID ->
            viewModel.openTask(taskID)
            NotificationManagerCompat.from(this)
                .cancel(LinkKeepAliveService.completionNotificationId(taskID))
            intent.removeExtra(EXTRA_TASK_ID)
        }
    }

    private fun importPairing(intent: android.content.Intent) {
        intent.getStringExtra(EXTRA_PAIRING)?.let(viewModel::pair)
        intent.getStringExtra(EXTRA_PAIRING_BASE64)?.let { encoded ->
            runCatching {
                Base64.decode(encoded, Base64.DEFAULT).decodeToString()
            }.getOrNull()?.let(viewModel::pair)
        }
    }

    companion object {
        const val EXTRA_PAIRING = "pairing"
        const val EXTRA_PAIRING_BASE64 = "pairingBase64"
        const val EXTRA_TASK_ID = "taskID"
    }
}
