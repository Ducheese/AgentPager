package com.agentgrid.mobile.ui

import androidx.activity.compose.BackHandler
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
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
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
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.zIndex
import com.agentgrid.mobile.AgentGridUiState
import com.agentgrid.mobile.ControlFeedback
import com.agentgrid.mobile.ControlFeedbackKind
import com.agentgrid.mobile.ReplySpeech
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
import kotlinx.coroutines.launch

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
    onToggleTerminalMode: () -> Unit,
    onOpenTaskConsumed: (String) -> Unit,
    onSpeechRateChange: (Float) -> Unit,
    onToggleReplySpeech: (String) -> Unit,
    inMultiWindow: Boolean,
    promptDrafts: Map<String, String>,
    onPromptDraftChange: (String, String) -> Unit,
    promptAttachments: Map<String, List<com.agentgrid.mobile.attachments.PromptAttachment>> = emptyMap(),
    onAddAttachments: (String, List<android.net.Uri>) -> Unit = { _, _ -> },
    onRemoveAttachment: (String, String) -> Unit = { _, _ -> },
    onRetryAttachment: (String, String) -> Unit = { _, _ -> },
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
                PairingScreen(state, onPair, inMultiWindow)
            } else {
                TaskTerminal(
                    state = state,
                    onUnpair = onUnpair,
                    onControl = onControl,
                    onFocus = onFocus,
                    onToggleDashboard = onToggleDashboard,
                    onToggleTerminalMode = onToggleTerminalMode,
                    onOpenTaskConsumed = onOpenTaskConsumed,
                    onSpeechRateChange = onSpeechRateChange,
                    onToggleReplySpeech = onToggleReplySpeech,
                    promptDrafts = promptDrafts,
                    onPromptDraftChange = onPromptDraftChange,
                    promptAttachments = promptAttachments,
                    onAddAttachments = onAddAttachments,
                    onRemoveAttachment = onRemoveAttachment,
                    onRetryAttachment = onRetryAttachment,
                )
            }
        }
    }
}

@Composable
private fun PairingScreen(
    state: AgentGridUiState,
    onPair: (String) -> Unit,
    inMultiWindow: Boolean,
) {
    var manualText by remember { mutableStateOf("") }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxSize()
            .background(AgentGridColors.Background)
            // edge-to-edge：状态栏/刘海/导航栏/键盘统一让位（配对页有粘贴输入框）
            .windowInsetsPadding(WindowInsets.safeDrawing),
    ) {
        val isCompact = maxWidth < 600.dp || maxHeight > maxWidth
        // 嵌套 lambda 里没有 BoxWithConstraintsScope 的隐式接收者，先落成本地值
        val availableWidth = maxWidth
        val availableHeight = maxHeight

        if (isCompact) {
            // 竖屏、分屏、悬浮小窗：单列排布。
            // 外层滚动防键盘遮挡；内层给足最小高度让内容真的垂直居中（短居中、长可滚）。
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(20.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = (availableHeight - 40.dp).coerceAtLeast(0.dp)),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Text(
                        "AGENTPAGER",
                        color = AgentGridColors.Text,
                        fontSize = 24.sp,
                        fontWeight = FontWeight.Bold,
                        letterSpacing = 2.sp,
                    )
                    Spacer(Modifier.height(14.dp))
                    // 竖屏取景框：3:4 竖框，宽高联合约束。相机是 16:9 横帧 + FILL_CENTER，
                    // 正方形框按宽撑满会把上下裁掉（取景框顶出屏幕外）；竖框裁的是左右，
                    // 上下完整，第一眼就是完整的框，不会误以为没对准。
                    ScannerArea(
                        inMultiWindow = inMultiWindow,
                        onResult = onPair,
                        modifier = Modifier
                            .widthIn(max = minOf(availableWidth * 0.78f, 300.dp))
                            .fillMaxWidth(fraction = 0.78f)
                            .aspectRatio(3f / 4f)
                            .heightIn(max = availableHeight * 0.38f),
                    )
                    Spacer(Modifier.height(10.dp))
                    Text("扫描 Bridge 上的二维码", color = AgentGridColors.Cyan, fontSize = 14.sp)
                    // 说明行 + 输入框收紧：之前 Spacer(14.dp) + 输入框 minLines=2，
                    // 内容总高顶出首屏，说明行下半截被输入框压住（见 2026-10-22 真机图）。
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "在电脑上打开 AgentPager Bridge → 扫描二维码",
                        color = AgentGridColors.Muted,
                        fontSize = 11.sp,
                        textAlign = TextAlign.Center,
                    )
                    Spacer(Modifier.height(10.dp))
                    OutlinedTextField(
                        value = manualText,
                        onValueChange = { manualText = it },
                        label = { Text("无法扫码？粘贴配对文本") },
                        minLines = 1,
                        maxLines = 4,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Spacer(Modifier.height(10.dp))
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
                            textAlign = TextAlign.Center,
                            modifier = Modifier.padding(top = 10.dp),
                        )
                    }
                }
            }
        } else {
            // 宽屏横屏桌面态：左右并排。右侧表单列带滚动——矮窗/键盘顶起来时不再整列被裁掉。
            Row(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(28.dp),
                horizontalArrangement = Arrangement.spacedBy(48.dp),
            ) {
                ScannerArea(
                    inMultiWindow = inMultiWindow,
                    onResult = onPair,
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxHeight(),
                )
                Column(
                    modifier = Modifier
                        .weight(0.92f)
                        .fillMaxHeight()
                        .verticalScroll(rememberScrollState()),
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = (availableHeight - 56.dp).coerceAtLeast(0.dp)),
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
}

/**
 * 扫码取景区。分屏/小窗模式下相机不可用（系统对非焦点多窗口封锁相机），
 * 此时不渲染相机，原位给提示；手动粘贴配对文本始终可用。
 *
 * 注意：这里刻意不画 border 取景框。相机是 16:9 横帧 + FILL_CENTER 按窄边撑满裁掉多余，
 * 解码走的是完整帧——Box 边缘的描边跟扫码区域没有任何对应关系，只会骗人：
 * 竖屏框上下被裁出屏幕、横屏框左右边压在画面正中看着像把画面切开。干净铺满就是取景区。
 */
@Composable
private fun ScannerArea(
    inMultiWindow: Boolean,
    onResult: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .background(AgentGridColors.Surface),
        contentAlignment = Alignment.Center,
    ) {
        if (inMultiWindow) {
            Column(
                modifier = Modifier.padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(
                    "分屏 / 小窗模式下无法使用相机扫码",
                    color = AgentGridColors.Amber,
                    fontSize = 12.sp,
                    textAlign = TextAlign.Center,
                )
                Text(
                    "请全屏后重试，或在下方粘贴配对文本",
                    color = AgentGridColors.Muted,
                    fontSize = 11.sp,
                    textAlign = TextAlign.Center,
                )
            }
        } else {
            QRCodeScanner(onResult = onResult, modifier = Modifier.fillMaxSize())
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
    onToggleTerminalMode: () -> Unit,
    onOpenTaskConsumed: (String) -> Unit,
    onSpeechRateChange: (Float) -> Unit,
    onToggleReplySpeech: (String) -> Unit,
    promptDrafts: Map<String, String>,
    onPromptDraftChange: (String, String) -> Unit,
    promptAttachments: Map<String, List<com.agentgrid.mobile.attachments.PromptAttachment>> = emptyMap(),
    onAddAttachments: (String, List<android.net.Uri>) -> Unit = { _, _ -> },
    onRemoveAttachment: (String, String) -> Unit = { _, _ -> },
    onRetryAttachment: (String, String) -> Unit = { _, _ -> },
) {
    val projection = state.taskProjection
    val tasks = projection.orderedTasks
    val showDashboard = projection.dashboardVisible
    val automaticallyExpandedTask = tasks.firstOrNull {
        it.id == projection.automaticallyExpandedTaskID
    }
    var expandedTaskID by remember { mutableStateOf<String?>(null) }
    var settingsVisible by remember { mutableStateOf(false) }
    // 相册 launcher 必须挂在无条件处：写进「展开 + 无待批请求」分支里时，
    // 选图期间状态一抖（心跳重组、待批请求弹出）launcher 就被注销，
    // 返回的 URI 投给已释放的回调——选完无反应的根因（2026-10-22 真机实测）。
    var pickerTaskID by remember { mutableStateOf<String?>(null) }
    val photoPicker = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.PickMultipleVisualMedia(
            com.agentgrid.mobile.attachments.AttachmentLimits.MAX_PER_PROMPT,
        ),
    ) { uris ->
        val target = pickerTaskID
        if (target != null && uris.isNotEmpty()) onAddAttachments(target, uris)
        pickerTaskID = null
    }
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

    // 层级返回：设置小窗 → 先关小窗；额度面板 → 回任务面板；都没开才交给系统（退桌面）。
    // 和点右上角图标是同一动作（onToggleDashboard / 关小窗），返回键只是另一只手。
    BackHandler(enabled = settingsVisible || showDashboard) {
        if (settingsVisible) settingsVisible = false
        else if (showDashboard) onToggleDashboard()
    }

    // 从完成通知进入：等任务进入可见集合（可能刚随快照到达），再展开并滚到它。
    LaunchedEffect(
        state.openTaskRequest,
        state.taskProjection.dashboardVisible,
        visibleTasks.map { it.id },
    ) {
        val taskID = state.openTaskRequest ?: return@LaunchedEffect
        if (state.taskProjection.dashboardVisible) return@LaunchedEffect
        val index = visibleTasks.indexOfFirst { it.id == taskID }
        if (index < 0) return@LaunchedEffect
        expandedTaskID = taskID
        taskListState.animateScrollToItem(index + 1)
        onOpenTaskConsumed(taskID)
    }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxSize()
            .background(AgentGridColors.Background)
            // Android 15+ 强制 edge-to-edge（decorFits 已失效）：状态栏/刘海/导航栏/键盘
            // 统一用 safeDrawing 让位，背景仍铺满；浮窗键盘 inset=0 时与收起一致。
            .windowInsetsPadding(WindowInsets.safeDrawing),
    ) {
        // 紧凑判定必须看**窗口**而不是键盘后的可用高度：竖屏键盘一弹，剩余高度可能小于宽度，
        // 用 maxHeight > maxWidth 会把布局误判成横屏（正好会把横屏的问题带回来）。
        val configuration = LocalConfiguration.current
        val isPortraitWindow = configuration.screenHeightDp > configuration.screenWidthDp
        val isCompact = maxWidth < 600.dp || isPortraitWindow
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
            modifier = Modifier
                .fillMaxSize()
                // 离线时底部给离线条预留独占 34dp（之前 overlay 直接压在列表上）；
                // 连上后还给 12dp，空间还给 task 列表。
                .padding(
                    top = 46.dp,
                    bottom = if (state.linkState == LinkState.CONNECTED) 12.dp else 34.dp,
                ),
            // 顶部 46dp 只留给右上角按钮；内容从下方开始铺满，不做横屏居中
            //（状态栏自己占一截之后，居中反而显得上下不靠）。
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
                            speakingTaskId = state.speakingTaskId,
                            onToggleReplySpeech = onToggleReplySpeech,
                                promptDraft = promptDrafts[task.id].orEmpty(),
                                onPromptDraftChange = onPromptDraftChange,
                                promptAttachments = promptAttachments[task.id].orEmpty(),
                                onAddAttachments = onAddAttachments,
                                onRemoveAttachment = onRemoveAttachment,
                                onRetryAttachment = onRetryAttachment,
                                onPhotoPickerLaunch = { taskID ->
                                    pickerTaskID = taskID
                                    photoPicker.launch(
                                        androidx.activity.result.PickVisualMediaRequest.Builder()
                                            .setMediaType(
                                                androidx.activity.result.contract.ActivityResultContracts.PickVisualMedia.ImageOnly,
                                            )
                                            .setMaxItems(
                                                com.agentgrid.mobile.attachments.AttachmentLimits.MAX_PER_PROMPT,
                                            )
                                            .build(),
                                    )
                                },
                                feedback = state.controlFeedback[task.id],
                                speechFailure = state.speechFailure,
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
                onSpeechRateChange = onSpeechRateChange,
                onUnpair = onUnpair,
                onToggleTerminalMode = onToggleTerminalMode,
                onControl = onControl,
                // 矮窗口（横屏/悬浮窗）下 6 行会顶出可视区：面板限高 + 内部滚动，键才点得到。
                panelMaxHeight = maxHeight - 60.dp,
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
    onAddAttachments: (String, List<android.net.Uri>) -> Unit = { _, _ -> },
    onRemoveAttachment: (String, String) -> Unit = { _, _ -> },
    onRetryAttachment: (String, String) -> Unit = { _, _ -> },
    onPhotoPickerLaunch: (String) -> Unit = {},
    promptAttachments: List<com.agentgrid.mobile.attachments.PromptAttachment> = emptyList(),
    task: TaskSnapshot,
    pending: PendingRequest?,
    expanded: Boolean,
    autoVisible: Boolean,
    dimmed: Boolean,
    showDivider: Boolean,
    compact: Boolean = false,
    speakingTaskId: String?,
    promptDraft: String,
    onPromptDraftChange: (String, String) -> Unit,
    feedback: ControlFeedback? = null,
    speechFailure: ReplySpeech.SpeechFailure? = null,
    onClick: () -> Unit,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
    onToggleReplySpeech: (String) -> Unit,
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
                speaking = speakingTaskId == task.id,
                promptDraft = promptDraft,
                onControl = onControl,
                onToggleReplySpeech = onToggleReplySpeech,
                onPromptDraftChange = onPromptDraftChange,
                promptAttachments = promptAttachments,
                onAddAttachments = onAddAttachments,
                onRemoveAttachment = onRemoveAttachment,
                onRetryAttachment = onRetryAttachment,
                onPhotoPickerLaunch = onPhotoPickerLaunch,
                feedback = feedback,
                speechFailure = speechFailure,
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
    speaking: Boolean = false,
    promptDraft: String,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
    onToggleReplySpeech: (String) -> Unit,
    onPromptDraftChange: (String, String) -> Unit,
    promptAttachments: List<com.agentgrid.mobile.attachments.PromptAttachment> = emptyList(),
    onAddAttachments: (String, List<android.net.Uri>) -> Unit = { _, _ -> },
    onRemoveAttachment: (String, String) -> Unit = { _, _ -> },
    onRetryAttachment: (String, String) -> Unit = { _, _ -> },
    onPhotoPickerLaunch: (String) -> Unit = {},
    feedback: ControlFeedback? = null,
    speechFailure: ReplySpeech.SpeechFailure? = null,
) {
    var answer by remember(task.id, pending?.summary) { mutableStateOf("") }
    val answerFeedbackActions = remember {
        setOf(ControlAction.APPROVE, ControlAction.DENY, ControlAction.ANSWER)
    }
    val promptFeedbackActions = remember { setOf(ControlAction.PROMPT) }
    val sending = feedback?.kind == ControlFeedbackKind.SENDING
    val answerBusy = feedback?.let {
        it.kind == ControlFeedbackKind.SENDING && it.action in answerFeedbackActions
    } == true
    val promptBusy = sending && feedback?.action == ControlAction.PROMPT
    // 非完成态也显示：第三行会被后续工具步顶掉，展开后的 REPLY 面板是唯一能留住中途正文的地方。
    // 三家在回合中都会喂 ResponseBody（agy 的 PostInvocation、opencode 的 reply 事件、
    // Claude 的心跳全文），只是以前这里卡了 SUCCEEDED 才把它挡住。
    val showResponseBody = showToken && !task.responseBody.isNullOrBlank()
    // TOOL 行（实验）：本轮最新一次工具输出；显示/隐藏条件与 REPLY 相同（展开且非空），
    // 清空也跟随 REPLY（新回合开始时由桥端把两者一起置空）。
    val showToolOutput = showToken && !task.latestToolOutput.isNullOrBlank()

    // 展开高度不封顶：内容多高就多高，滚动交给外层 LazyColumn；
    // 之前 heightIn(max) + 内层 verticalScroll 会把长 REPLY/TOOL 截断成框内滚动。
    Column(
        modifier = Modifier
            .fillMaxWidth()
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
            if (showToolOutput) {
                ToolSection(task.latestToolOutput!!)
            }
            if (showResponseBody) {
                ResponseSection(
                    markdown = task.responseBody!!,
                    speaking = speaking,
                    canSpeak = task.isTerminal,
                    failure = speechFailure?.takeIf { it.taskID == task.id }?.message,
                    onToggle = { onToggleReplySpeech(task.id) },
                )
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
                        PixelButton("允许", AgentGridColors.Green, enabled = !answerBusy) {
                            onControl(task.id, ControlAction.APPROVE, null, null)
                        }
                        PixelButton("拒绝", AgentGridColors.Red, enabled = !answerBusy) {
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
                            minLines = 3,
                            maxLines = if (compact) 8 else 5,
                        )
                        PixelButton(
                            "发送回答",
                            AgentGridColors.Yellow,
                            enabled = !answerBusy && (picked.isNotEmpty() || answer.isNotBlank()),
                        ) {
                            if (picked.isNotEmpty() || answer.isNotBlank()) {
                                onControl(
                                    task.id,
                                    ControlAction.ANSWER,
                                    answer.ifBlank { null },
                                    picked,
                                )
                            }
                        }
                    } else {
                        targetPending.options.forEach { option ->
                            PixelButton(option, AgentGridColors.Cyan, enabled = !answerBusy) {
                                onControl(task.id, ControlAction.ANSWER, option, null)
                            }
                        }
                        OutlinedTextField(
                            value = answer,
                            onValueChange = { answer = it },
                            modifier = Modifier.fillMaxWidth(),
                            minLines = 3,
                            maxLines = if (compact) 8 else 5,
                        )
                        PixelButton(
                            "发送回答",
                            AgentGridColors.Yellow,
                            enabled = !answerBusy && answer.isNotBlank(),
                        ) {
                            if (answer.isNotBlank()) {
                                onControl(task.id, ControlAction.ANSWER, answer, null)
                            }
                        }
                    }
                }
                ControlFeedbackLine(feedback, answerFeedbackActions)
            }
        }
        // 与 MODEL/TOKEN/REPLY 同一条可见性规则：收纳时一起收缩，
        // 否则收起动画里只剩一个输入框裸着顶到 lastStep 下面（就是"区别对待"的来源）。
        if (showToken && pending == null && TaskCapability.PROMPT in task.capabilities) {
            val bringIntoViewRequester = remember { BringIntoViewRequester() }
            val scope = rememberCoroutineScope()
            // 输入框持有 TextFieldValue（带光标）：[图N] 要按整块删除，必须知道这次删了哪一段。
            // 文本仍由 ViewModel 的 promptDraft 托管，删图重排后靠 LaunchedEffect 回灌。
            var draftField by remember(task.id) { mutableStateOf(TextFieldValue(promptDraft)) }
            LaunchedEffect(promptDraft) {
                if (promptDraft != draftField.text) {
                    val cursor = draftField.selection.end.coerceIn(0, promptDraft.length)
                    draftField = TextFieldValue(promptDraft, TextRange(cursor))
                }
            }
            // 图片条：输入框上方横排，图N角标 + [+]插符 + [x]删除 + 状态遮罩。
            if (promptAttachments.isNotEmpty()) {
                androidx.compose.foundation.lazy.LazyRow(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    itemsIndexed(promptAttachments, key = { _, item -> item.clientId }) { index, item ->
                        AttachmentCard(
                            index = index,
                            attachment = item,
                            enabled = !promptBusy,
                            onInsert = {
                                // 插在光标处（有选区则替换选区），而不是永远追加到末尾。
                                val token = "[图${index + 1}]"
                                val start = draftField.selection.min
                                val end = draftField.selection.max
                                val next = draftField.text.replaceRange(start, end, token)
                                draftField = TextFieldValue(next, TextRange(start + token.length))
                                onPromptDraftChange(task.id, next)
                            },
                            onRemove = { onRemoveAttachment(task.id, item.clientId) },
                            onRetry = { onRetryAttachment(task.id, item.clientId) },
                        )
                    }
                }
            }
            if (compact) {
                // 竖屏：输入框独占一行全宽（3~8 行），[+]/发送键另起一行——
                // 横排三件套在窄屏下把输入框挤成一条缝，多行内容滚出可视区。
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .bringIntoViewRequester(bringIntoViewRequester),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    PromptInputField(
                        draftField = draftField,
                        onDraftChange = { next ->
                            draftField = next
                            onPromptDraftChange(task.id, next.text)
                        },
                        placeholder = if (task.lifecycle == AgentLifecycle.RUNNING) "追加 Prompt (排队)..." else "输入 Prompt 发起任务...",
                        minLines = 3,
                        maxLines = 8,
                        modifier = Modifier.fillMaxWidth(),
                        onFocused = { scope.launch { bringIntoViewRequester.bringIntoView() } },
                    )
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        // 相册入口：+ 号方框。launcher 在 TaskTerminal 无条件处，这里只登记任务。
                        Box(
                            modifier = Modifier
                                .size(40.dp)
                                .border(1.dp, AgentGridColors.Divider)
                                .clickable(enabled = !promptBusy) { onPhotoPickerLaunch(task.id) },
                            contentAlignment = Alignment.Center,
                        ) {
                            Text("＋", color = AgentGridColors.Cyan, fontSize = 18.sp, fontWeight = FontWeight.Bold)
                        }
                        // 全部 READY 才放行：序号 = 卡片序号 = 清单序号，不给"失败图占号又被静默清掉"留缝。
                        val blocking = promptAttachments.any {
                            it.state != com.agentgrid.mobile.attachments.AttachmentState.READY
                        }
                        PixelButton(
                            if (task.lifecycle == AgentLifecycle.RUNNING) "排队" else "发送",
                            AgentGridColors.Cyan,
                            enabled = !promptBusy && !blocking && draftField.text.isNotBlank(),
                        ) {
                            if (draftField.text.isNotBlank()) {
                                // 草稿由 ViewModel 在「已确认送达」后清空；失败/超时保留原文
                                onControl(task.id, ControlAction.PROMPT, draftField.text.trim(), null)
                            }
                        }
                    }
                }
            } else {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .bringIntoViewRequester(bringIntoViewRequester),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    // 相册入口：输入框左侧，+ 号方框。launcher 在 TaskTerminal 无条件处，这里只登记任务。
                    Box(
                        modifier = Modifier
                            .size(40.dp)
                            .border(1.dp, AgentGridColors.Divider)
                            .clickable(enabled = !promptBusy) { onPhotoPickerLaunch(task.id) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text("＋", color = AgentGridColors.Cyan, fontSize = 18.sp, fontWeight = FontWeight.Bold)
                    }
                    PromptInputField(
                        draftField = draftField,
                        onDraftChange = { next ->
                            draftField = next
                            onPromptDraftChange(task.id, next.text)
                        },
                        placeholder = if (task.lifecycle == AgentLifecycle.RUNNING) "追加 Prompt (排队)..." else "输入 Prompt 发起任务...",
                        minLines = 1,
                        maxLines = 5,
                        modifier = Modifier.weight(1f),
                        onFocused = { scope.launch { bringIntoViewRequester.bringIntoView() } },
                    )
                    // 全部 READY 才放行：序号 = 卡片序号 = 清单序号，不给"失败图占号又被静默清掉"留缝。
                    val blocking = promptAttachments.any {
                        it.state != com.agentgrid.mobile.attachments.AttachmentState.READY
                    }
                    PixelButton(
                        if (task.lifecycle == AgentLifecycle.RUNNING) "排队" else "发送",
                        AgentGridColors.Cyan,
                        enabled = !promptBusy && !blocking && draftField.text.isNotBlank(),
                    ) {
                        if (draftField.text.isNotBlank()) {
                            // 草稿由 ViewModel 在「已确认送达」后清空；失败/超时保留原文
                            onControl(task.id, ControlAction.PROMPT, draftField.text.trim(), null)
                        }
                    }
                }
            }
            ControlFeedbackLine(feedback, promptFeedbackActions)
        }
    }
}

/**
 * Prompt / question 共用的多行输入框。[图N] 原子删除、键盘弹起滚到可见区都在这里。
 * 高度由 minLines/maxLines 决定：maxLines 之后 TextField 内部滚动，不再顶布局。
 */
@Composable
private fun PromptInputField(
    draftField: TextFieldValue,
    onDraftChange: (TextFieldValue) -> Unit,
    placeholder: String,
    minLines: Int,
    maxLines: Int,
    modifier: Modifier = Modifier,
    onFocused: () -> Unit,
) {
    OutlinedTextField(
        value = draftField,
        onValueChange = { value ->
            // 退格/选区删除落在 [图N] 里时，扩成整块删除，不留半截占位符。
            val atomic = com.agentgrid.mobile.attachments.PromptTokens.expandAtomicDeletion(
                draftField.text,
                value.text,
            )
            val next = if (atomic != null) {
                TextFieldValue(atomic.text, TextRange(atomic.cursor))
            } else {
                value
            }
            onDraftChange(next)
        },
        modifier = modifier
            .onFocusChanged { focusState ->
                // 键盘弹起后把输入框滚到可见区（嵌套滚动也会向上传递，列表跟着让位）
                if (focusState.isFocused) {
                    onFocused()
                }
            },
        placeholder = {
            Text(
                placeholder,
                color = AgentGridColors.Muted,
                fontSize = 12.sp,
            )
        },
        minLines = minLines,
        maxLines = maxLines,
    )
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
private fun ToolSection(output: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(
            "TOOL",
            color = AgentGridColors.Amber,
            fontSize = 9.sp,
            modifier = Modifier
                .width(58.dp)
                .padding(vertical = 3.dp),
        )
        Box(
            modifier = Modifier
                .weight(1f)
                .background(AgentGridColors.SurfaceRaised.copy(alpha = 0.6f))
                .padding(8.dp),
        ) {
            // 高度不封顶：跟随 TaskDetails 自适应展开，滚动交给外层 LazyColumn。
            Column {
                Text(
                    output,
                    color = AgentGridColors.Text.copy(alpha = 0.9f),
                    fontSize = 9.sp,
                    lineHeight = 13.sp,
                    fontFamily = FontFamily.Monospace,
                )
            }
        }
    }
}

@Composable
private fun ResponseSection(
    markdown: String,
    speaking: Boolean,
    canSpeak: Boolean,
    onToggle: () -> Unit,
    failure: String? = null,
) {
    // 完成态才给「开读」；已在朗读时永远可停（哪怕任务又变回运行态，不留下停不掉的死角）。
    val clickable = speaking || canSpeak
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            // 不加粗（2026-10-16）：REPLY 原来带 FontWeight.Bold，而 PixelFontFamily 把同一个 ttf
            // 同时注册成 Normal/Medium/Bold —— 没有独立粗体字面，系统就做**伪粗体**，描边向外双向扩，
            // 左边缘跟着多出一点，三个标签里就它看着偏左。去掉后与 MODEL/TOKEN 同字面、同原点，
            // 标签列严格对齐。
            Text(
                text = if (speaking) "STOP" else "REPLY",
                color = if (speaking) AgentGridColors.Red else AgentGridColors.Green,
                fontSize = 9.sp,
                modifier = Modifier
                    .width(58.dp)
                    .clickable(
                        enabled = clickable,
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null,
                        onClick = onToggle,
                    )
                    .padding(vertical = 3.dp),
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
        if (failure != null) {
            Text(failure, color = AgentGridColors.Red, fontSize = 9.sp, lineHeight = 12.sp)
        }
    }
}

@Composable
private fun PixelButton(
    label: String,
    color: Color,
    enabled: Boolean = true,
    action: () -> Unit,
) {
    OutlinedButton(
        onClick = action,
        enabled = enabled,
        shape = RectangleShape,
        colors = ButtonDefaults.outlinedButtonColors(contentColor = color),
        contentPadding = ButtonDefaults.TextButtonContentPadding,
    ) {
        Text(label, fontSize = 10.sp)
    }
}

/**
 * 附图卡片：64dp 方块，左上「图N」角标、右上 [x] 删除、底部 [+] 插入占位符。
 * 上传中盖半透明遮罩+文字进度，失败标红点按重试，READY 显示正常。
 */
@Composable
private fun AttachmentCard(
    index: Int,
    attachment: com.agentgrid.mobile.attachments.PromptAttachment,
    enabled: Boolean,
    onInsert: () -> Unit,
    onRemove: () -> Unit,
    onRetry: () -> Unit,
) {
    val label = "图${index + 1}"
    Box(
        modifier = Modifier
            .size(64.dp)
            .background(AgentGridColors.SurfaceRaised)
            .border(
                1.dp,
                when (attachment.state) {
                    com.agentgrid.mobile.attachments.AttachmentState.READY -> AgentGridColors.Cyan
                    com.agentgrid.mobile.attachments.AttachmentState.FAILED -> AgentGridColors.Red
                    else -> AgentGridColors.Divider
                },
            )
            .clickable(
                enabled = enabled &&
                    attachment.state != com.agentgrid.mobile.attachments.AttachmentState.COMPRESSING &&
                    attachment.state != com.agentgrid.mobile.attachments.AttachmentState.UPLOADING,
            ) {
                if (attachment.state == com.agentgrid.mobile.attachments.AttachmentState.FAILED) onRetry()
                else onInsert()
            },
        contentAlignment = Alignment.Center,
    ) {
        // 真缩略图：压缩时顺手生成 192px JPEG，READY 后大图字节释放、缩略图留着。
        val thumbBitmap = androidx.compose.runtime.remember(attachment.clientId, attachment.thumb) {
            attachment.thumb?.let { bytes ->
                android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            }
        }
        if (thumbBitmap != null) {
            androidx.compose.foundation.Image(
                bitmap = thumbBitmap.asImageBitmap(),
                contentDescription = label,
                modifier = Modifier.fillMaxSize(),
                contentScale = androidx.compose.ui.layout.ContentScale.Crop,
            )
            // 压一层淡黑，保证角标和状态字在亮图上可读。
            Box(
                Modifier
                    .fillMaxSize()
                    .background(androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.25f)),
            )
        } else {
            Text(
                label,
                color = AgentGridColors.Text,
                fontSize = 13.sp,
                fontWeight = FontWeight.Bold,
            )
        }
        // 左上序号角标
        Text(
            label,
            color = AgentGridColors.Background,
            fontSize = 8.sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier
                .align(Alignment.TopStart)
                .background(AgentGridColors.Cyan)
                .padding(horizontal = 4.dp, vertical = 1.dp),
        )
        // 右上删除
        Text(
            "×",
            color = AgentGridColors.Red,
            fontSize = 13.sp,
            fontWeight = FontWeight.Bold,
            modifier = Modifier
                .align(Alignment.TopEnd)
                .clickable(enabled = enabled) { onRemove() }
                .padding(horizontal = 5.dp, vertical = 1.dp),
        )
        // 底部插入符 / 状态
        val bottomText = when (attachment.state) {
            com.agentgrid.mobile.attachments.AttachmentState.COMPRESSING -> "压缩中…"
            com.agentgrid.mobile.attachments.AttachmentState.UPLOADING -> "上传中…"
            com.agentgrid.mobile.attachments.AttachmentState.FAILED -> "失败点重试"
            com.agentgrid.mobile.attachments.AttachmentState.READY -> "[+]插入"
        }
        Text(
            bottomText,
            color = when (attachment.state) {
                com.agentgrid.mobile.attachments.AttachmentState.FAILED -> AgentGridColors.Red
                com.agentgrid.mobile.attachments.AttachmentState.READY -> AgentGridColors.Green
                else -> AgentGridColors.Muted
            },
            fontSize = 8.sp,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 3.dp),
        )
    }
}

/** 一行极简回执：发送中… / 已发送 / 失败原因。不弹提示，失败时原文保留可重发。 */
@Composable
private fun ControlFeedbackLine(
    feedback: ControlFeedback?,
    actions: Set<ControlAction>,
) {
    if (feedback == null || feedback.action !in actions) return
    val text = when (feedback.kind) {
        ControlFeedbackKind.SENDING -> "发送中…"
        ControlFeedbackKind.SENT -> "已发送"
        ControlFeedbackKind.FAILED -> feedback.message ?: "发送失败"
    }
    val color = when (feedback.kind) {
        ControlFeedbackKind.SENDING -> AgentGridColors.Cyan
        ControlFeedbackKind.SENT -> AgentGridColors.Green
        ControlFeedbackKind.FAILED -> AgentGridColors.Red
    }
    Text(text, color = color, fontSize = 9.sp, lineHeight = 12.sp)
}

@Composable
private fun SettingsPanel(
    state: AgentGridUiState,
    soundEnabled: Boolean,
    onSoundEnabledChange: (Boolean) -> Unit,
    onSpeechRateChange: (Float) -> Unit,
    onUnpair: () -> Unit,
    onToggleTerminalMode: () -> Unit,
    onControl: (String, ControlAction, String?, List<String>?) -> Unit,
    panelMaxHeight: Dp,
) {
    Column(
        modifier = Modifier
            .width(206.dp)
            .background(AgentGridColors.Surface)
            .heightIn(max = panelMaxHeight)
            .verticalScroll(rememberScrollState())
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
        PixelButton(
            "朗读语速：${formatSpeechRate(state.speechRate)}",
            AgentGridColors.Muted,
        ) {
            onSpeechRateChange(nextSpeechRate(state.speechRate))
        }
        PixelButton(
            if (state.terminalMode) "专用模式：开" else "专用模式：关",
            if (state.terminalMode) AgentGridColors.Green else AgentGridColors.Muted,
        ) {
            onToggleTerminalMode()
        }
        PixelButton("解除配对", AgentGridColors.Red) { onUnpair() }
        // 关机：点两次才发（第一次布防，8 秒内没点第二次自动撤防），误触保护。
        // 发出去电脑 60 秒后关机（桥侧 shutdown /t 60），留反悔窗口，可在电脑上 shutdown /a 取消。
        var shutdownArmed by remember { mutableStateOf(false) }
        if (shutdownArmed) {
            LaunchedEffect(Unit) {
                delay(8000)
                shutdownArmed = false
            }
        }
        PixelButton(
            if (shutdownArmed) "确认关机？" else "关闭计算机",
            AgentGridColors.Red,
        ) {
            if (shutdownArmed) {
                shutdownArmed = false
                onControl("", ControlAction.SHUTDOWN, null, null)
            } else {
                shutdownArmed = true
            }
        }
    }
}

private val SpeechRates = listOf(1.0f, 1.25f, 1.5f, 1.75f, 2.0f)

private fun nextSpeechRate(current: Float): Float {
    val index = SpeechRates.indexOfFirst { kotlin.math.abs(it - current) < 0.01f }
    return SpeechRates[if (index < 0) SpeechRates.lastIndex else (index + 1) % SpeechRates.size]
}

private fun formatSpeechRate(rate: Float): String =
    String.format(java.util.Locale.US, "%.2f", rate).trimEnd('0').trimEnd('.') + "x"

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
