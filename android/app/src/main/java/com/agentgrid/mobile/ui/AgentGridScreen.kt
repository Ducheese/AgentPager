package com.agentgrid.mobile.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.SizeTransform
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.zIndex
import com.agentgrid.mobile.AgentGridUiState
import com.agentgrid.mobile.SoundEngine
import com.agentgrid.mobile.TaskSoundTracker
import com.agentgrid.mobile.domain.AgentActivity
import com.agentgrid.mobile.domain.AgentLifecycle
import com.agentgrid.mobile.domain.AgentSource
import com.agentgrid.mobile.domain.ControlAction
import com.agentgrid.mobile.domain.PendingRequest
import com.agentgrid.mobile.domain.PendingRequestKind
import com.agentgrid.mobile.domain.SubagentSnapshot
import com.agentgrid.mobile.domain.TaskCapability
import com.agentgrid.mobile.domain.TaskSnapshot
import com.agentgrid.mobile.domain.UsageWindow
import com.agentgrid.mobile.network.LinkState
import com.agentgrid.mobile.render.PixelCoreSurfaceView
import com.agentgrid.mobile.render.PixelRenderState
import kotlinx.coroutines.delay

private val EaseOutQuart = CubicBezierEasing(0.25f, 1f, 0.5f, 1f)
private val TaskListAnchorHeight = 1.dp
private const val TaskListAnchorKey = "agentgrid-task-list-anchor"

internal fun taskListAnchorScrollOffsetPx(anchorHeightPx: Int): Int {
    require(anchorHeightPx > 0) { "任务列表锚点高度必须大于零" }
    return anchorHeightPx - 1
}

@Composable
private fun Modifier.newVisibleTaskEntrance(enabled: Boolean): Modifier {
    var settled by remember(enabled) { mutableStateOf(!enabled) }
    val offsetY by animateDpAsState(
        targetValue = if (settled) 0.dp else (-8).dp,
        animationSpec = tween(
            durationMillis = 180,
            easing = EaseOutQuart,
        ),
        label = "新可见任务顶部落位",
    )

    LaunchedEffect(enabled) {
        if (enabled) {
            settled = true
        }
    }

    // 只改变绘制层位置，不参与列表测量，避免和 animateItem 的换位动画互相干扰。
    return graphicsLayer {
        translationY = offsetY.toPx()
    }
}

@Composable
fun AgentGridScreen(
    state: AgentGridUiState,
    onPair: (String) -> Unit,
    onUnpair: () -> Unit,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
    onFocus: (String) -> Unit,
    onToggleDashboard: () -> Unit,
    onExitTerminal: () -> Unit,
) {
    AgentGridTheme {
        AnimatedContent(
            targetState = state.pairing == null,
            transitionSpec = {
                (
                    (
                        fadeIn(tween(180)) +
                            scaleIn(
                                initialScale = 0.985f,
                                animationSpec = tween(210),
                            )
                    ) togetherWith (
                        fadeOut(tween(120)) +
                            scaleOut(
                                targetScale = 0.985f,
                                animationSpec = tween(170),
                            )
                    )
                ).using(SizeTransform(clip = false))
            },
            label = "配对与终端切换",
        ) { isPairing ->
            if (isPairing) {
                PairingScreen(state, onPair)
            } else {
                TaskTerminal(
                    state = state,
                    onUnpair = onUnpair,
                    onControl = onControl,
                    onFocus = onFocus,
                    onToggleDashboard = onToggleDashboard,
                    onExitTerminal = onExitTerminal,
                )
            }
        }
    }
}

@Composable
private fun PairingScreen(
    state: AgentGridUiState,
    onPair: (String) -> Unit,
) {
    var manualText by remember { mutableStateOf("") }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxSize()
            .background(AgentGridColors.Background),
    ) {
        val isCompact = maxWidth < 600.dp || maxHeight > maxWidth

        if (isCompact) {
            // 竖屏、分屏、悬浮小窗：单列垂直居中排布，带滚动防键盘遮挡
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(20.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Text(
                    "AGENTPAGER",
                    color = AgentGridColors.Text,
                    fontSize = 24.sp,
                    fontWeight = FontWeight.Bold,
                    letterSpacing = 2.sp,
                )
                Box(
                    modifier = Modifier
                        .size(190.dp)
                        .background(AgentGridColors.Surface),
                ) {
                    QRCodeScanner(onResult = onPair, modifier = Modifier.fillMaxSize())
                }
                Text("扫描 Bridge 上的二维码", color = AgentGridColors.Cyan, fontSize = 14.sp)
                Text(
                    "在电脑上打开 AgentPager Bridge → 扫描二维码",
                    color = AgentGridColors.Muted,
                    fontSize = 11.sp,
                )
                OutlinedTextField(
                    value = manualText,
                    onValueChange = { manualText = it },
                    label = { Text("无法扫码？粘贴配对文本") },
                    minLines = 2,
                    maxLines = 4,
                    modifier = Modifier.fillMaxWidth(),
                )
                Button(
                    onClick = { onPair(manualText) },
                    enabled = manualText.isNotBlank(),
                    shape = RectangleShape,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text("连接 Bridge")
                }
                AnimatedVisibility(visible = state.pairingError != null) {
                    Text(
                        state.pairingError.orEmpty(),
                        color = AgentGridColors.Red,
                        fontSize = 12.sp,
                    )
                }
            }
        } else {
            // 宽屏横屏桌面态：左右并排
            Row(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(28.dp),
                horizontalArrangement = Arrangement.spacedBy(48.dp),
            ) {
                Box(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxHeight()
                        .background(AgentGridColors.Surface),
                ) {
                    QRCodeScanner(onResult = onPair, modifier = Modifier.fillMaxSize())
                }
                Column(
                    modifier = Modifier
                        .weight(0.92f)
                        .fillMaxHeight(),
                    verticalArrangement = Arrangement.Center,
                ) {
                    Text(
                        "AGENTPAGER",
                        color = AgentGridColors.Text,
                        fontSize = 30.sp,
                        fontWeight = FontWeight.Bold,
                        letterSpacing = 2.sp,
                    )
                    Spacer(Modifier.height(10.dp))
                    Text("扫描 Bridge 上的二维码", color = AgentGridColors.Cyan, fontSize = 16.sp)
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "在电脑上打开 AgentPager Bridge → 连接，\n扫描其中显示的二维码",
                        color = AgentGridColors.Muted,
                        fontSize = 12.sp,
                        lineHeight = 18.sp,
                    )
                    Spacer(Modifier.height(18.dp))
                    OutlinedTextField(
                        value = manualText,
                        onValueChange = { manualText = it },
                        label = { Text("无法扫码？粘贴配对文本") },
                        minLines = 3,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(10.dp))
                    Button(
                        onClick = { onPair(manualText) },
                        enabled = manualText.isNotBlank(),
                        shape = RectangleShape,
                    ) {
                        Text("连接 Bridge")
                    }
                    AnimatedVisibility(visible = state.pairingError != null) {
                        Column {
                            Spacer(Modifier.height(10.dp))
                            Text(
                                state.pairingError.orEmpty(),
                                color = AgentGridColors.Red,
                                fontSize = 12.sp,
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun TaskTerminal(
    state: AgentGridUiState,
    onUnpair: () -> Unit,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
    onFocus: (String) -> Unit,
    onToggleDashboard: () -> Unit,
    onExitTerminal: () -> Unit,
) {
    val projection = state.taskProjection
    val tasks = projection.orderedTasks
    val showDashboard = projection.dashboardVisible
    val automaticallyExpandedTask = tasks.firstOrNull {
        it.id == projection.automaticallyExpandedTaskID
    }
    var expandedTaskID by remember { mutableStateOf<String?>(null) }
    var settingsVisible by remember { mutableStateOf(false) }
    val visibleTasks = projection.visibleTasks
    val enteringVisibleTaskIDs = projection.enteringVisibleTaskIDs
    val taskListAnchorHeightPx = with(LocalDensity.current) {
        TaskListAnchorHeight.roundToPx()
    }
    val taskListState = rememberLazyListState(
        initialFirstVisibleItemIndex = 0,
        initialFirstVisibleItemScrollOffset = taskListAnchorScrollOffsetPx(
            taskListAnchorHeightPx,
        ),
    )
    val context = LocalContext.current
    val sound = remember(context) { SoundEngine(context.applicationContext) }
    val soundTracker = remember { TaskSoundTracker() }
    var soundEnabled by remember(sound) { mutableStateOf(sound.isEnabled) }

    DisposableEffect(sound) {
        onDispose(sound::close)
    }

    LaunchedEffect(tasks.map { Triple(it.id, it.lifecycle, it.isMuted) }) {
        soundTracker.nextCue(tasks, soundEnabled)?.let(sound::play)
    }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxSize()
            .background(AgentGridColors.Background),
    ) {
        val isCompact = maxWidth < 600.dp || maxHeight > maxWidth
        val isVeryNarrow = maxWidth < 400.dp

        TopControls(
            windows = if (showDashboard || isVeryNarrow) {
                emptyList()
            } else if (isCompact) {
                state.usage?.windows.orEmpty().take(1)
            } else {
                state.usage?.windows.orEmpty()
            },
            settingsVisible = settingsVisible,
            dashboardVisible = showDashboard,
            onToggleDashboard = {
                onToggleDashboard()
                settingsVisible = false
            },
            onToggleSettings = { settingsVisible = !settingsVisible },
            modifier = Modifier
                .align(Alignment.TopEnd)
                .zIndex(2f),
        )

        AnimatedContent(
            targetState = showDashboard,
            transitionSpec = {
                (
                    (
                        fadeIn(tween(180)) +
                            scaleIn(
                                initialScale = 0.985f,
                                animationSpec = tween(210),
                            )
                    ) togetherWith (
                        fadeOut(tween(120)) +
                            scaleOut(
                                targetScale = 0.985f,
                                animationSpec = tween(170),
                            )
                    )
                ).using(SizeTransform(clip = false))
            },
            modifier = if (isCompact) {
                Modifier
                    .fillMaxSize()
                    .padding(top = 46.dp, bottom = 12.dp)
            } else {
                Modifier.align(Alignment.Center)
            },
            label = "任务态与状态态切换",
        ) { isDashboardVisible ->
            if (isDashboardVisible) {
                UsageDashboard(
                    usage = state.usage,
                    usages = state.usages,
                    connected = state.linkState == LinkState.CONNECTED,
                )
            } else {
                LazyColumn(
                    state = taskListState,
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = if (isCompact) 12.dp else 20.dp)
                        .then(
                            if (isCompact) Modifier.fillMaxHeight()
                            else Modifier.heightIn(max = 306.dp)
                        ),
                    verticalArrangement = Arrangement.spacedBy(0.dp),
                ) {
                    item(key = TaskListAnchorKey) {
                        // 保留一个物理像素在视口内，让这个固定项承担 LazyColumn 的滚动锚点。
                        Spacer(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(TaskListAnchorHeight),
                        )
                    }
                    itemsIndexed(
                        items = visibleTasks,
                        key = { _, task -> task.id },
                    ) { index, task ->
                        val entersFromTop = remember(task.id) {
                            task.id in enteringVisibleTaskIDs
                        }
                        val pending = state.pendingRequests.firstOrNull {
                            it.taskID == task.id
                        }
                        TaskRow(
                            modifier = Modifier
                                // 自动置顶的任务在换位期间保持前景层，避免从原首行下方穿过。
                                .zIndex(
                                    if (task.id == automaticallyExpandedTask?.id) 1f else 0f,
                                )
                                .animateItem(
                                    fadeInSpec = tween(
                                        durationMillis = 180,
                                        easing = EaseOutQuart,
                                    ),
                                    placementSpec = spring(
                                        // 临界阻尼让远距离换位自然多用一点时间，同时避免回弹。
                                        dampingRatio = 1f,
                                        stiffness = 520f,
                                    ),
                                    fadeOutSpec = tween(120),
                                )
                                .newVisibleTaskEntrance(entersFromTop),
                            task = task,
                            pending = pending,
                            expanded = expandedTaskID == task.id,
                            autoVisible = task.id == projection.automaticallyExpandedTaskID,
                            dimmed = projection.urgentTaskID != null &&
                                projection.urgentTaskID != task.id,
                            showDivider = index < visibleTasks.lastIndex,
                            compact = isCompact,
                            onClick = {
                                val willExpand = expandedTaskID != task.id
                                expandedTaskID = if (willExpand) task.id else null
                                if (task.isUnread) {
                                    onControl(task.id, ControlAction.MARK_READ, null, null)
                                }
                                onFocus(task.id)
                            },
                            onControl = onControl,
                        )
                    }
                }
            }
        }

        AnimatedVisibility(
            visible = settingsVisible,
            enter = fadeIn(tween(140)),
            exit = fadeOut(tween(110)),
            modifier = Modifier
                .align(Alignment.TopEnd)
                .zIndex(1f)
                .padding(top = 42.dp, end = 18.dp),
        ) {
            SettingsPanel(
                state = state,
                soundEnabled = soundEnabled,
                onSoundEnabledChange = { enabled ->
                    sound.setEnabled(enabled)
                    soundEnabled = enabled
                },
                onUnpair = onUnpair,
                onExitTerminal = onExitTerminal,
            )
        }

        AnimatedVisibility(
            visible = state.linkState != LinkState.CONNECTED,
            enter = fadeIn(tween(140)) +
                slideInVertically(tween(140)) { fullHeight -> fullHeight / 2 },
            exit = fadeOut(tween(110)) +
                slideOutVertically(tween(110)) { fullHeight -> fullHeight / 2 },
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 10.dp),
        ) {
            Text(
                "BRIDGE OFFLINE · 正在重连",
                color = AgentGridColors.Dimmed,
                fontSize = 10.sp,
                letterSpacing = 1.sp,
            )
        }
    }
}

@Composable
private fun TopControls(
    windows: List<UsageWindow>,
    settingsVisible: Boolean,
    dashboardVisible: Boolean,
    onToggleDashboard: () -> Unit,
    onToggleSettings: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier.padding(top = 10.dp, end = 18.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        windows.take(2).forEach { window ->
            UsagePill(window)
        }
        Box(
            modifier = Modifier
                .size(28.dp)
                .clickable(onClick = onToggleDashboard)
                .semantics {
                    contentDescription = if (dashboardVisible) {
                        "切换到任务列表"
                    } else {
                        "切换到状态显示栏"
                    }
            },
            contentAlignment = Alignment.Center,
        ) {
            ViewSwitchIcon(showTaskIcon = dashboardVisible)
        }
        Box(
            modifier = Modifier
                .size(28.dp)
                .clickable(onClick = onToggleSettings)
                .semantics {
                    contentDescription = if (settingsVisible) "关闭设置" else "打开设置"
                },
            contentAlignment = Alignment.Center,
        ) {
            Text(
                if (settingsVisible) "×" else "⚙",
                color = if (settingsVisible) AgentGridColors.Amber else AgentGridColors.Muted,
                fontSize = 13.sp,
                fontWeight = FontWeight.Bold,
            )
        }
    }
}

@Composable
private fun ViewSwitchIcon(showTaskIcon: Boolean) {
    Canvas(modifier = Modifier.size(12.dp)) {
        val unit = size.minDimension / 16f

        fun pixelRect(x: Float, y: Float, width: Float, height: Float) {
            drawRect(
                color = AgentGridColors.Muted,
                topLeft = Offset(x * unit, y * unit),
                size = Size(width * unit, height * unit),
            )
        }

        if (showTaskIcon) {
            // 三行任务列表：左侧状态格，右侧信息条。
            listOf(1f, 7f, 13f).forEach { y ->
                pixelRect(x = 0f, y = y, width = 3f, height = 3f)
                pixelRect(x = 5f, y = y, width = 11f, height = 3f)
            }
        } else {
            // 三档状态柱：用硬直角高度差表示状态总览。
            pixelRect(x = 0f, y = 10f, width = 4f, height = 6f)
            pixelRect(x = 6f, y = 6f, width = 4f, height = 10f)
            pixelRect(x = 12f, y = 1f, width = 4f, height = 15f)
        }
    }
}

@Composable
private fun UsagePill(window: UsageWindow) {
    val color = when {
        window.remainingPercentage < 10 -> AgentGridColors.Red
        window.remainingPercentage < 20 -> AgentGridColors.Orange
        else -> AgentGridColors.Cyan
    }
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(7.dp),
    ) {
        Text(
            "${window.label} ${window.remainingPercentage.toInt()}%",
            color = AgentGridColors.Muted,
            fontSize = 9.sp,
        )
        LinearProgressIndicator(
            progress = { (window.remainingPercentage / 100).toFloat() },
            modifier = Modifier
                .width(56.dp)
                .height(3.dp),
            color = color,
            trackColor = AgentGridColors.Divider,
            gapSize = 0.dp,
            drawStopIndicator = {},
        )
    }
}

@Composable
private fun TaskRow(
    modifier: Modifier = Modifier,
    task: TaskSnapshot,
    pending: PendingRequest?,
    expanded: Boolean,
    autoVisible: Boolean,
    dimmed: Boolean,
    showDivider: Boolean,
    compact: Boolean = false,
    onClick: () -> Unit,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
) {
    val rowAlpha by animateFloatAsState(
        targetValue = if (dimmed) 0.34f else 1f,
        animationSpec = tween(180),
        label = "任务行注意力",
    )
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(if (expanded || autoVisible) AgentGridColors.Surface else Color.Transparent)
            .padding(horizontal = 4.dp),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(if (compact) 72.dp else 78.dp)
                .clickable(onClick = onClick),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            AndroidView(
                factory = { PixelCoreSurfaceView(it) },
                update = {
                    it.updateState(
                        PixelRenderState(
                            lifecycle = task.lifecycle,
                            activity = task.activity,
                            // 模拟器重复点击同一状态时也应从头播放；真实任务不随普通内容更新重置。
                            revision = if (task.id == "agentgrid-simulator") task.updatedAt else 0,
                        ),
                    )
                    it.alpha = rowAlpha
                },
                modifier = Modifier
                    .size(if (compact) 60.dp else 70.dp)
                    .semantics { contentDescription = "任务状态 ${statusText(task.lifecycle)}" },
            )

            Column(
                modifier = Modifier
                    .weight(1f)
                    .padding(start = if (compact) 6.dp else 8.dp, end = if (compact) 8.dp else 14.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                Text(
                    task.title,
                    color = AgentGridColors.Text.copy(alpha = rowAlpha),
                    fontSize = if (compact) 14.sp else 15.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Column(
                    modifier = Modifier.offset(y = (-2).dp),
                    verticalArrangement = Arrangement.spacedBy(0.dp),
                ) {
                    Text(
                        task.userPrompt?.let { if (task.promptQueued) "你（排队中）：$it" else "你：$it" } ?: "你：—",
                        color = AgentGridColors.Muted.copy(alpha = rowAlpha),
                        fontSize = 11.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    CommandTicker(
                        text = task.latestStep ?: statusText(task.lifecycle),
                        color = statusColor(task).copy(alpha = rowAlpha),
                    )
                }
            }

            TaskMeta(task = task, alpha = rowAlpha, compact = compact)
        }

        AnimatedVisibility(
            visible = expanded || autoVisible,
            enter = expandVertically(
                animationSpec = tween(
                    durationMillis = 280,
                    easing = EaseOutQuart,
                ),
                expandFrom = Alignment.Top,
            ) + fadeIn(
                animationSpec = tween(
                    durationMillis = 180,
                    delayMillis = 35,
                    easing = EaseOutQuart,
                ),
            ),
            exit = shrinkVertically(
                animationSpec = tween(
                    durationMillis = 190,
                    easing = EaseOutQuart,
                ),
                shrinkTowards = Alignment.Top,
            ) + fadeOut(tween(120)),
        ) {
            TaskDetails(
                task = task,
                pending = pending,
                showToken = expanded,
                compact = compact,
                onControl = onControl,
            )
        }

        if (showDivider) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = if (compact) 68.dp else 78.dp, end = 4.dp)
                    .height(1.dp)
                    .background(AgentGridColors.Divider.copy(alpha = 0.85f)),
            )
        }
    }
}

@Composable
private fun CommandTicker(
    text: String,
    color: Color,
) {
    AnimatedContent(
        targetState = text,
        transitionSpec = {
            (
                (
                    slideInVertically(
                        animationSpec = tween(
                            durationMillis = 220,
                            easing = EaseOutQuart,
                        ),
                    ) { fullHeight -> fullHeight } +
                        fadeIn(
                            animationSpec = tween(
                                durationMillis = 160,
                                easing = EaseOutQuart,
                            ),
                        )
                ) togetherWith (
                    slideOutVertically(
                        animationSpec = tween(
                            durationMillis = 160,
                            easing = EaseOutQuart,
                        ),
                    ) { fullHeight -> -fullHeight } +
                        fadeOut(tween(110))
                )
            ).using(SizeTransform(clip = true))
        },
        contentAlignment = Alignment.CenterStart,
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 19.dp)
            .offset(y = (-3).dp),
        label = "AI 命令更新",
    ) { currentText ->
        Text(
            currentText,
            color = color,
            fontSize = 10.sp,
            lineHeight = 14.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun TaskMeta(task: TaskSnapshot, alpha: Float, compact: Boolean = false) {
    val elapsed by produceState(
        initialValue = task.elapsedAt(System.currentTimeMillis()),
        task.id,
        task.lifecycle,
        task.startedAt,
        task.updatedAt,
        task.completedAt,
    ) {
        if (task.lifecycle == AgentLifecycle.RUNNING || task.lifecycle == AgentLifecycle.STARTING) {
            while (true) {
                value = task.elapsedAt(System.currentTimeMillis())
                delay(1_000)
            }
        }
    }
    Column(
        modifier = if (compact) {
            Modifier.wrapContentWidth(Alignment.End)
        } else {
            Modifier.widthIn(min = 166.dp, max = 210.dp)
        },
        horizontalAlignment = Alignment.End,
        verticalArrangement = Arrangement.spacedBy(if (compact) 4.dp else 7.dp),
    ) {
        Row(
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            MetaTag(agentBadge(task.source), agentBadgeColor(task.source), alpha)
            if (!compact) {
                MetaTag(
                    task.modelName ?: agentOriginLabel(task.source),
                    AgentGridColors.Muted,
                    alpha,
                    modifier = Modifier.weight(1f, fill = false),
                )
            }
        }
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(if (compact) 6.dp else 10.dp),
        ) {
            if (!compact) {
                Text(
                    formatTokens(task.tokenUsage?.total),
                    color = AgentGridColors.Muted.copy(alpha = alpha),
                    fontSize = 9.sp,
                )
            }
            Text(
                formatElapsed(elapsed),
                color = AgentGridColors.Muted.copy(alpha = alpha),
                fontSize = 9.sp,
            )
            Box(
                modifier = Modifier
                    .size(7.dp)
                    .background(statusColor(task).copy(alpha = alpha)),
            )
        }
    }
}

@Composable
private fun MetaTag(
    text: String,
    color: Color,
    alpha: Float,
    modifier: Modifier = Modifier,
) {
    Text(
        text,
        color = color.copy(alpha = alpha),
        fontSize = 9.sp,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = modifier
            .background(AgentGridColors.SurfaceRaised.copy(alpha = alpha))
            .padding(horizontal = 7.dp, vertical = 4.dp),
    )
}

@Composable
private fun TaskDetails(
    task: TaskSnapshot,
    pending: PendingRequest?,
    showToken: Boolean,
    compact: Boolean = false,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
) {
    var answer by remember(task.id, pending?.summary) { mutableStateOf("") }
    val showResponseBody = showToken &&
        task.lifecycle == AgentLifecycle.SUCCEEDED &&
        !task.responseBody.isNullOrBlank()

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(max = if (showResponseBody) (if (compact) 480.dp else 380.dp) else (if (compact) 320.dp else 250.dp))
            .verticalScroll(rememberScrollState())
            .padding(start = if (compact) 68.dp else 82.dp, end = if (compact) 8.dp else 18.dp, bottom = 8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        if (task.subagents.isNotEmpty()) {
            SubagentPanel(task.subagents, agentBadge(task.source).uppercase())
        }
        if (showToken) {
            if (compact) {
                DetailLine(
                    "MODEL",
                    task.modelName ?: agentOriginLabel(task.source),
                    AgentGridColors.Cyan,
                )
            }
            task.tokenUsage?.let {
                DetailLine(
                    "TOKEN",
                    tokenDetailsText(task),
                    AgentGridColors.Cyan,
                )
            }
            if (showResponseBody) {
                ResponseSection(markdown = task.responseBody!!)
            }
        }
        AnimatedContent(
            targetState = pending,
            contentKey = { it?.kind },
            transitionSpec = {
                (
                    (
                        fadeIn(tween(180)) +
                            scaleIn(
                                initialScale = 0.985f,
                                animationSpec = tween(210),
                            )
                    ) togetherWith (
                        fadeOut(tween(120)) +
                            scaleOut(
                                targetScale = 0.985f,
                                animationSpec = tween(170),
                            )
                    )
                ).using(SizeTransform(clip = false))
            },
            label = "待处理请求切换",
        ) { targetPending ->
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                targetPending?.summary?.let { summary ->
                    DetailLine(
                        if (targetPending.kind == PendingRequestKind.APPROVAL) {
                            "APPROVAL"
                        } else {
                            "QUESTION"
                        },
                        summary,
                        if (targetPending.kind == PendingRequestKind.APPROVAL) {
                            AgentGridColors.Amber
                        } else {
                            AgentGridColors.Yellow
                        },
                    )
                }

                if (
                    targetPending?.kind == PendingRequestKind.APPROVAL &&
                    TaskCapability.APPROVE in task.capabilities
                ) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        PixelButton("允许", AgentGridColors.Green) {
                            onControl(task.id, ControlAction.APPROVE, null, null)
                        }
                        PixelButton("拒绝", AgentGridColors.Red) {
                            onControl(task.id, ControlAction.DENY, null, null)
                        }
                    }
                } else if (
                    targetPending?.kind == PendingRequestKind.QUESTION &&
                    TaskCapability.ANSWER in task.capabilities
                ) {
                    // 单选题：点一下即作答——把按钮上的文本（`label（注解）`）原样发回去，
                    // 桥端对表还原成纯 label，走结构化 answers（而不是自由文本 response）。
                    //
                    // 多选题（桥端从 Claude 的 multiSelect / opencode 的 multiple 取来）：
                    // 选项变成可反复点选的开关，攒够了由「发送回答」一次性提交（走 ControlPayload.values）。
                    // 打字内容两种题都可以带——Claude 那边它算"再选一项"。
                    if (targetPending.multiSelect) {
                        var picked by remember(task.id, targetPending.summary) { mutableStateOf(emptyList<String>()) }
                        targetPending.options.forEach { option ->
                            val on = option in picked
                            PixelButton(
                                if (on) "✓ $option" else option,
                                if (on) AgentGridColors.Green else AgentGridColors.Cyan,
                            ) {
                                picked = if (on) picked - option else picked + option
                            }
                        }
                        OutlinedTextField(
                            value = answer,
                            onValueChange = { answer = it },
                            modifier = Modifier.fillMaxWidth(),
                            maxLines = 3,
                        )
                        PixelButton("发送回答", AgentGridColors.Yellow) {
                            if (picked.isNotEmpty() || answer.isNotBlank()) {
                                val toSend = answer.ifBlank { null }
                                val optionsToSend = picked
                                answer = ""
                                picked = emptyList()
                                onControl(
                                    task.id,
                                    ControlAction.ANSWER,
                                    toSend,
                                    optionsToSend,
                                )
                            }
                        }
                    } else {
                        targetPending.options.forEach { option ->
                            PixelButton(option, AgentGridColors.Cyan) {
                                answer = ""
                                onControl(task.id, ControlAction.ANSWER, option, null)
                            }
                        }
                        OutlinedTextField(
                            value = answer,
                            onValueChange = { answer = it },
                            modifier = Modifier.fillMaxWidth(),
                            maxLines = 3,
                        )
                        PixelButton("发送回答", AgentGridColors.Yellow) {
                            if (answer.isNotBlank()) {
                                val toSend = answer
                                answer = ""
                                onControl(task.id, ControlAction.ANSWER, toSend, null)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SubagentPanel(subagents: List<SubagentSnapshot>, label: String) {
    val now by produceState(
        initialValue = System.currentTimeMillis(),
        key1 = subagents.map { it.id to it.lifecycle },
    ) {
        while (true) {
            value = System.currentTimeMillis()
            delay(1_000)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(AgentGridColors.SurfaceRaised)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(9.dp),
    ) {
        Text(
            "$label (${subagents.size})",
            color = AgentGridColors.Muted,
            fontSize = 10.sp,
            fontWeight = FontWeight.Bold,
        )
        subagents.forEach { subagent ->
            key(subagent.id) {
                SubagentRow(subagent, now)
            }
        }
    }
}

@Composable
private fun SubagentRow(
    subagent: SubagentSnapshot,
    now: Long,
) {
    val color = lifecycleColor(subagent.lifecycle, subagent.activity)
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(6.dp)
                    .background(color),
            )
            Text(
                subagent.displayName,
                color = AgentGridColors.Text,
                fontSize = 11.sp,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            subagent.tokenUsage?.takeIf { it.total > 0 }?.let {
                Text(
                    formatTokens(it.total),
                    color = AgentGridColors.Muted,
                    fontSize = 8.sp,
                )
            }
            Text(
                "${statusText(subagent.lifecycle)} · ${formatElapsed(subagent.elapsedAt(now))}",
                color = color,
                fontSize = 9.sp,
            )
        }
        Text(
            "└ ${subagent.latestStep ?: activityText(subagent.activity)}",
            color = AgentGridColors.Muted,
            fontSize = 9.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 14.dp),
        )
    }
}

@Composable
private fun DetailLine(label: String, value: String, color: Color) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(label, color = color, fontSize = 9.sp, modifier = Modifier.width(58.dp))
        Text(value, color = AgentGridColors.Muted, fontSize = 10.sp)
    }
}

@Composable
private fun ResponseSection(markdown: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(
            "REPLY",
            color = AgentGridColors.Green,
            fontSize = 9.sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier.width(58.dp),
        )
        Box(
            modifier = Modifier
                .weight(1f)
                .background(AgentGridColors.SurfaceRaised.copy(alpha = 0.6f))
                .padding(8.dp),
        ) {
            MarkdownView(markdown = markdown)
        }
    }
}

@Composable
private fun PixelButton(
    label: String,
    color: Color,
    action: () -> Unit,
) {
    OutlinedButton(
        onClick = action,
        shape = RectangleShape,
        colors = ButtonDefaults.outlinedButtonColors(contentColor = color),
        contentPadding = ButtonDefaults.TextButtonContentPadding,
    ) {
        Text(label, fontSize = 10.sp)
    }
}

@Composable
private fun SettingsPanel(
    state: AgentGridUiState,
    soundEnabled: Boolean,
    onSoundEnabledChange: (Boolean) -> Unit,
    onUnpair: () -> Unit,
    onExitTerminal: () -> Unit,
) {
    Column(
        modifier = Modifier
            .width(206.dp)
            .background(AgentGridColors.Surface)
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text("设置", color = AgentGridColors.Text, fontSize = 13.sp, fontWeight = FontWeight.Bold)
        DetailLine(
            "LINK",
            if (state.linkState == LinkState.CONNECTED) "已连接" else "正在重连",
            if (state.linkState == LinkState.CONNECTED) AgentGridColors.Green else AgentGridColors.Red,
        )
        DetailLine("TASK", "${state.taskProjection.orderedTasks.size}", AgentGridColors.Cyan)
        PixelButton(
            if (soundEnabled) "提示音：开" else "提示音：关",
            if (soundEnabled) AgentGridColors.Green else AgentGridColors.Muted,
        ) {
            onSoundEnabledChange(!soundEnabled)
        }
        PixelButton("解除配对", AgentGridColors.Red, onUnpair)
        PixelButton("退出专用模式", AgentGridColors.Muted, onExitTerminal)
    }
}

private fun statusText(lifecycle: AgentLifecycle): String = when (lifecycle) {
    AgentLifecycle.OFFLINE -> "离线"
    AgentLifecycle.IDLE -> "空闲"
    AgentLifecycle.STARTING -> "正在启动"
    AgentLifecycle.RUNNING -> "正在运行"
    AgentLifecycle.WAITING_APPROVAL -> "等待批准"
    AgentLifecycle.WAITING_ANSWER -> "等待回答"
    AgentLifecycle.SUCCEEDED -> "任务完成"
    AgentLifecycle.INTERRUPTED -> "已中断"
}

private fun statusColor(task: TaskSnapshot): Color =
    lifecycleColor(task.lifecycle, task.activity)

private fun lifecycleColor(
    lifecycle: AgentLifecycle,
    activity: AgentActivity?,
): Color = when (lifecycle) {
    AgentLifecycle.WAITING_APPROVAL -> AgentGridColors.Amber
    AgentLifecycle.WAITING_ANSWER -> AgentGridColors.Yellow
    AgentLifecycle.SUCCEEDED -> AgentGridColors.Green
    AgentLifecycle.INTERRUPTED, AgentLifecycle.OFFLINE -> AgentGridColors.Muted
    AgentLifecycle.IDLE -> AgentGridColors.Cyan
    AgentLifecycle.STARTING -> AgentGridColors.Violet
    AgentLifecycle.RUNNING -> when (activity) {
        AgentActivity.READING, AgentActivity.SEARCHING, AgentActivity.BROWSING ->
            AgentGridColors.Cyan
        AgentActivity.EDITING -> AgentGridColors.Indigo
        AgentActivity.EXECUTING -> AgentGridColors.Orange
        AgentActivity.TESTING -> AgentGridColors.Blue
        else -> AgentGridColors.Violet
    }
}

private fun agentBadge(source: AgentSource): String = when (source) {
    AgentSource.CODEX_DESKTOP, AgentSource.CODEX_CLI -> "Codex"
    AgentSource.CLAUDE_CODE -> "Claude"
    AgentSource.OPENCODE -> "OpenCode"
    AgentSource.ANTIGRAVITY -> "AGY"
}

private fun agentBadgeColor(source: AgentSource): Color = when (source) {
    AgentSource.CLAUDE_CODE -> AgentGridColors.Violet
    AgentSource.OPENCODE -> AgentGridColors.Green
    AgentSource.ANTIGRAVITY -> AgentGridColors.Cyan
    else -> AgentGridColors.Blue
}

private fun agentOriginLabel(source: AgentSource): String = when (source) {
    AgentSource.CODEX_DESKTOP -> "ChatGPT.app"
    AgentSource.CODEX_CLI -> "Terminal"
    AgentSource.CLAUDE_CODE -> "Claude Code"
    AgentSource.OPENCODE -> "opencode"
    AgentSource.ANTIGRAVITY -> "Antigravity"
}

private fun activityText(activity: AgentActivity?): String = when (activity) {
    AgentActivity.THINKING -> "正在思考"
    AgentActivity.READING -> "正在读取"
    AgentActivity.SEARCHING -> "正在搜索"
    AgentActivity.EDITING -> "正在编辑"
    AgentActivity.EXECUTING -> "正在执行"
    AgentActivity.TESTING -> "正在测试"
    AgentActivity.BROWSING -> "正在浏览"
    AgentActivity.DELEGATING -> "正在协作"
    null -> "等待新步骤"
}

private fun formatElapsed(milliseconds: Long): String {
    val totalSeconds = milliseconds / 1_000
    val hours = totalSeconds / 3_600
    val minutes = totalSeconds % 3_600 / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) {
        "%d:%02d:%02d".format(hours, minutes, seconds)
    } else {
        "%02d:%02d".format(minutes, seconds)
    }
}

private fun formatTokens(value: Int?): String {
    val tokens = value ?: return "— tok"
    return when {
        tokens >= 1_000_000 -> "%.1fm tok".format(tokens / 1_000_000.0)
        tokens >= 1_000 -> "%.1fk tok".format(tokens / 1_000.0)
        else -> "$tokens tok"
    }
}

private fun tokenDetailsText(task: TaskSnapshot): String {
    val usage = task.tokenUsage ?: return "—"
    return "IN ${formatTokens(usage.input)} · CACHE ${formatTokens(usage.cachedInput)} · " +
        "OUT ${formatTokens(usage.output)} · TOTAL ${formatTokens(usage.total)}"
}
