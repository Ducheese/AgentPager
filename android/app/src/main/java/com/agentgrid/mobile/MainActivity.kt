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
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.agentgrid.mobile.ui.AgentGridScreen

import android.content.res.Configuration

class MainActivity : ComponentActivity() {
    private val viewModel: AgentGridViewModel by viewModels()

    // 分屏/自由小窗状态：扫码界面据此决定是否渲染相机（多窗口下相机被系统封锁）
    private val multiWindowState = androidx.compose.runtime.mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        multiWindowState.value = isInMultiWindowMode
        updateSystemBars()
        importPairing(intent)

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
                onExitTerminal = {
                    viewModel.setTerminalMode(false)
                    exitTerminalMode()
                },
                onToggleReplySpeech = viewModel::toggleReplySpeech,
                inMultiWindow = multiWindowState.value,
                promptDrafts = viewModel.promptDrafts,
                onPromptDraftChange = viewModel::setPromptDraft,
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
        importPairing(intent)
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
        val isLandscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        // 分屏、小窗或竖屏模式下退出全屏沉浸，让系统手柄与系统栏正常可用；全屏横屏下保持沉浸
        if (!isInMultiWindowMode && isLandscape && viewModel.state.value.terminalMode) {
            enterTerminalMode()
        } else {
            exitTerminalMode()
        }
    }

    private fun enterTerminalMode() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    private fun exitTerminalMode() {
        WindowCompat.setDecorFitsSystemWindows(window, true)
        WindowInsetsControllerCompat(window, window.decorView)
            .show(WindowInsetsCompat.Type.systemBars())
        if (!viewModel.state.value.terminalMode) {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
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
    }
}
