package com.agentgrid.mobile.ui

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

/**
 * 让 Compose Dialog 的独立窗口跟随 Activity 的系统栏可见状态。
 *
 * Android 的 Dialog 是**另一个 Window**：Activity 上用 insets controller `hide(systemBars())`
 * 得到的沉浸态不会继承过去。横屏专用模式下主界面本来就是无栏的，一开全屏对话框系统栏
 * 重新出现，`WindowInsets.safeDrawing` 于是把状态栏高度也算进内边距——竖屏看是正确的避让，
 * 横屏就变成顶部一条纯浪费的带子（2026-11-02 用户报出；相关改动见 45ec862 / 9632b47）。
 *
 * 做法：读 Activity 根 view 当前的 inset 可见性；Activity 隐藏了系统栏，就在对话框自己的
 * 窗口里同样隐藏。safeDrawing 随之收缩到只剩 displayCutout（摄像头/刘海区域仍然避让）。
 * Activity 本来就没隐藏（竖屏、分屏、小窗）时什么都不做，保持原来的避让行为。
 */
@Composable
internal fun MatchActivitySystemBars() {
    val view = LocalView.current
    val activity = LocalContext.current.findActivity()
    LaunchedEffect(Unit) {
        val activityDecor = activity?.window?.decorView ?: return@LaunchedEffect
        val activityInsets = ViewCompat.getRootWindowInsets(activityDecor) ?: return@LaunchedEffect
        if (activityInsets.isVisible(WindowInsetsCompat.Type.systemBars())) return@LaunchedEffect

        val controller = ViewCompat.getWindowInsetsController(view) ?: return@LaunchedEffect
        controller.hide(WindowInsetsCompat.Type.systemBars())
        controller.systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}
