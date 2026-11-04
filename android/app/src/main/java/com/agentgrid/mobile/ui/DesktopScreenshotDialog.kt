package com.agentgrid.mobile.ui

import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.rememberTransformableState
import androidx.compose.foundation.gestures.transformable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.sizeIn
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.agentgrid.mobile.DesktopClickState
import com.agentgrid.mobile.DesktopScreenshotState
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlinx.coroutines.delay

/**
 * 桌面全屏截图对话框：
 * - 顶部工具栏：时间戳、[ 刷新 ]、[ 关闭 ]
 * - 画布区域：双指缩放拖动、双击缩放切换、系统返回键退出
 */
@Composable
fun DesktopScreenshotDialog(
    state: DesktopScreenshotState,
    clickState: DesktopClickState,
    onRefresh: () -> Unit,
    onSendClick: (Float, Float, String, Boolean, String, Int, Int) -> Unit,
    onResetClick: () -> Unit,
    onDismiss: () -> Unit,
) {
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(
            usePlatformDefaultWidth = false,
            decorFitsSystemWindows = false,
        ),
    ) {
        BackHandler(onBack = onDismiss)
        // 横屏专用模式下 Activity 已隐藏系统栏；对话框是独立窗口，需同样隐藏，
        // 否则 safeDrawing 会把状态栏高度算成内边距，顶部白白浪费一条。
        MatchActivitySystemBars()

        var savedScale by remember { mutableFloatStateOf(1f) }
        var savedOffset by remember { mutableStateOf(Offset.Zero) }
        val isRefreshing = state is DesktopScreenshotState.Loading

        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(AgentGridColors.Background)
                .windowInsetsPadding(WindowInsets.safeDrawing),
        ) {
            // 顶部工具栏
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(AgentGridColors.SurfaceRaised)
                    .padding(horizontal = 14.dp, vertical = 10.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Row(
                    modifier = Modifier.weight(1f),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = "DESKTOP",
                        color = AgentGridColors.Cyan,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (state is DesktopScreenshotState.Success) {
                        // 桥端快门时间 + 实际走的腿 + 从点击到收到图片的总耗时。
                        val detail = remember(
                            state.capturedAt,
                            state.viaTunnel,
                            state.elapsedMillis,
                        ) {
                            val timeStr = SimpleDateFormat("HH:mm:ss", Locale.getDefault())
                                .format(Date(state.capturedAt))
                            val route = if (state.viaTunnel) "TUNNEL" else "LAN"
                            val seconds = state.elapsedMillis / 1000.0
                            "$timeStr · $route · ${String.format(Locale.getDefault(), "%.1fs", seconds)}"
                        }
                        Text(
                            text = detail,
                            color = AgentGridColors.Muted,
                            fontSize = 11.sp,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(14.dp),
                ) {
                    Text(
                        text = "[ 刷新 ]",
                        color = if (isRefreshing) AgentGridColors.Dimmed else AgentGridColors.Cyan,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier.clickable(enabled = !isRefreshing) { onRefresh() },
                    )
                    Text(
                        text = "[ 关闭 ]",
                        color = AgentGridColors.Amber,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier.clickable { onDismiss() },
                    )
                }
            }

            // 画布与内容区
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                contentAlignment = Alignment.Center,
            ) {
                when (state) {
                    is DesktopScreenshotState.Idle,
                    is DesktopScreenshotState.Loading -> {
                        Column(
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.Center,
                        ) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(32.dp),
                                color = AgentGridColors.Cyan,
                                strokeWidth = 2.dp,
                            )
                            Spacer(modifier = Modifier.height(12.dp))
                            Text(
                                text = "正在截取桌面画面...",
                                color = AgentGridColors.Muted,
                                fontSize = 12.sp,
                            )
                        }
                    }

                    is DesktopScreenshotState.Error -> {
                        Column(
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.Center,
                            modifier = Modifier.padding(24.dp),
                        ) {
                            Text(
                                text = "截取桌面失败",
                                color = AgentGridColors.Red,
                                fontSize = 14.sp,
                                fontWeight = FontWeight.Bold,
                            )
                            Spacer(modifier = Modifier.height(8.dp))
                            Text(
                                text = state.message,
                                color = AgentGridColors.Muted,
                                fontSize = 12.sp,
                                textAlign = TextAlign.Center,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Spacer(modifier = Modifier.height(16.dp))
                            Text(
                                text = "[ 重试 ]",
                                color = AgentGridColors.Cyan,
                                fontSize = 13.sp,
                                fontWeight = FontWeight.Bold,
                                modifier = Modifier.clickable { onRefresh() },
                            )
                        }
                    }

                    is DesktopScreenshotState.Success -> {
                        DesktopScreenshotCanvas(
                            bitmap = state.bitmap,
                            viaTunnel = state.viaTunnel,
                            clickState = clickState,
                            initialScale = savedScale,
                            initialOffset = savedOffset,
                            onTransformChanged = { newScale, newOffset ->
                                savedScale = newScale
                                savedOffset = newOffset
                            },
                            onRefresh = onRefresh,
                            onSendClick = onSendClick,
                            onResetClick = onResetClick,
                        )
                    }
                }
            }
        }
    }
}

private data class PendingClick(
    val anchor: Offset,
    val normalized: Offset,
    val requestId: String = java.util.UUID.randomUUID().toString(),
)

/**
 * 具有平滑双指捏合缩放、拖动画布平移、双击智能切换缩放比例的图片展示区域。
 * 长按会按当前缩放/平移反向映射回原图像素，再弹出「发送点击」。
 */
@Composable
private fun DesktopScreenshotCanvas(
    bitmap: Bitmap,
    viaTunnel: Boolean,
    clickState: DesktopClickState,
    initialScale: Float,
    initialOffset: Offset,
    onTransformChanged: (Float, Offset) -> Unit,
    onRefresh: () -> Unit,
    onSendClick: (Float, Float, String, Boolean, String, Int, Int) -> Unit,
    onResetClick: () -> Unit,
) {
    val imageBitmap = remember(bitmap) { bitmap.asImageBitmap() }
    val haptic = LocalHapticFeedback.current
    var scale by remember { mutableFloatStateOf(initialScale) }
    var offset by remember { mutableStateOf(initialOffset) }
    var clickTarget by remember { mutableStateOf<PendingClick?>(null) }

    BoxWithConstraints(
        modifier = Modifier
            .fillMaxSize()
            .clipToBounds(),
        contentAlignment = Alignment.Center,
    ) {
        val containerWidth = constraints.maxWidth.toFloat()
        val containerHeight = constraints.maxHeight.toFloat()

        // 计算图片在 ContentScale.Fit 模式下的实际视觉宽高，消除黑边留白区域导致的过度移动
        val imageAspect = if (bitmap.height > 0) bitmap.width.toFloat() / bitmap.height.toFloat() else 1f
        val containerAspect = if (containerHeight > 0) containerWidth / containerHeight else 1f

        val displayedWidth = if (imageAspect > containerAspect) {
            containerWidth
        } else {
            containerHeight * imageAspect
        }
        val displayedHeight = if (imageAspect > containerAspect) {
            containerWidth / imageAspect
        } else {
            containerHeight
        }

        // 确保继承的 offset 在视口与图片渲染实际边界内合法
        LaunchedEffect(containerWidth, containerHeight, bitmap.width, bitmap.height) {
            val maxOffsetX = ((displayedWidth * scale - containerWidth) / 2f).coerceAtLeast(0f)
            val maxOffsetY = ((displayedHeight * scale - containerHeight) / 2f).coerceAtLeast(0f)
            if (scale > 1f) {
                offset = Offset(
                    x = offset.x.coerceIn(-maxOffsetX, maxOffsetX),
                    y = offset.y.coerceIn(-maxOffsetY, maxOffsetY),
                )
            } else {
                offset = Offset.Zero
            }
            onTransformChanged(scale, offset)
        }

        val transformableState = rememberTransformableState { zoomChange, panChange, _ ->
            if (zoomChange != 1f || (scale > 1f && panChange != Offset.Zero)) {
                clickTarget = null
                onResetClick()
            }
            val newScale = (scale * zoomChange).coerceIn(1f, 6f)
            scale = newScale

            // 依据图片实际渲染边界计算移动上限，彻底消除上下大黑边时的过度移位
            val maxOffsetX = ((displayedWidth * scale - containerWidth) / 2f).coerceAtLeast(0f)
            val maxOffsetY = ((displayedHeight * scale - containerHeight) / 2f).coerceAtLeast(0f)

            if (scale > 1f) {
                offset = Offset(
                    x = (offset.x + panChange.x).coerceIn(-maxOffsetX, maxOffsetX),
                    y = (offset.y + panChange.y).coerceIn(-maxOffsetY, maxOffsetY),
                )
            } else {
                offset = Offset.Zero
            }
            onTransformChanged(scale, offset)
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .transformable(state = transformableState)
                .pointerInput(bitmap, containerWidth, containerHeight, displayedWidth, displayedHeight) {
                    detectTapGestures(
                        onLongPress = { tapOffset ->
                            val normalized = DesktopScreenshotGeometry.toImageNormalized(
                                tap = tapOffset,
                                container = Size(containerWidth, containerHeight),
                                displayed = Size(displayedWidth, displayedHeight),
                                scale = scale,
                                translation = offset,
                            )
                            if (normalized != null) {
                                onResetClick()
                                clickTarget = PendingClick(
                                    anchor = tapOffset,
                                    normalized = normalized,
                                    requestId = java.util.UUID.randomUUID().toString(),
                                )
                                haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            }
                        },
                        onDoubleTap = { tapOffset ->
                            clickTarget = null
                            onResetClick()
                            if (scale > 1f) {
                                scale = 1f
                                offset = Offset.Zero
                            } else {
                                val doubleTapScale = 2.5f
                                scale = doubleTapScale
                                // 聚焦到双击点
                                val targetOffsetX =
                                    (containerWidth / 2f - tapOffset.x) * (doubleTapScale - 1f)
                                val targetOffsetY =
                                    (containerHeight / 2f - tapOffset.y) * (doubleTapScale - 1f)
                                val maxOffsetX =
                                    ((displayedWidth * doubleTapScale - containerWidth) / 2f)
                                        .coerceAtLeast(0f)
                                val maxOffsetY =
                                    ((displayedHeight * doubleTapScale - containerHeight) / 2f)
                                        .coerceAtLeast(0f)
                                offset = Offset(
                                    x = targetOffsetX.coerceIn(-maxOffsetX, maxOffsetX),
                                    y = targetOffsetY.coerceIn(-maxOffsetY, maxOffsetY),
                                )
                            }
                            onTransformChanged(scale, offset)
                        },
                    )
                },
            contentAlignment = Alignment.Center,
        ) {
            Image(
                bitmap = imageBitmap,
                contentDescription = "桌面全屏截图",
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer {
                        scaleX = scale
                        scaleY = scale
                        translationX = offset.x
                        translationY = offset.y
                    },
                contentScale = ContentScale.Fit,
            )
        }

        clickTarget?.let { target ->
            ClickTargetOverlay(
                target = target,
                bitmap = bitmap,
                containerWidth = containerWidth,
                containerHeight = containerHeight,
                clickState = clickState,
                onSend = { action ->
                    onSendClick(
                        target.normalized.x,
                        target.normalized.y,
                        action,
                        viaTunnel,
                        target.requestId,
                        bitmap.width,
                        bitmap.height,
                    )
                },
                onCancel = {
                    clickTarget = null
                    onResetClick()
                },
                onSent = {
                    clickTarget = null
                    onResetClick()
                    onRefresh()
                },
            )
        }
    }
}

@Composable
private fun ClickTargetOverlay(
    target: PendingClick,
    bitmap: Bitmap,
    containerWidth: Float,
    containerHeight: Float,
    clickState: DesktopClickState,
    onSend: (String) -> Unit,
    onCancel: () -> Unit,
    onSent: () -> Unit,
) {
    val density = LocalDensity.current
    val haptic = LocalHapticFeedback.current

    // 绑定 target.requestId，防止切换定标点时脏数据污染
    var activeAction by remember(target.requestId) { mutableStateOf<String?>("left") }
    var barWidthPx by remember(target.requestId) { mutableFloatStateOf(0f) }
    var totalHeightPx by remember(target.requestId) { mutableFloatStateOf(0f) }

    val popupHeight = 28.dp
    val margin = with(density) { 10.dp.toPx() }
    val estimatedWidthPx = with(density) { 216.dp.toPx() }
    val popupHeightPx = with(density) { popupHeight.toPx() }
    val effectiveBarWidthPx = if (barWidthPx > 0f) barWidthPx else estimatedWidthPx
    val effectiveTotalHeightPx = if (totalHeightPx > 0f) totalHeightPx else popupHeightPx

    val markerSize = 28.dp
    val reticleRadius = with(density) { (markerSize / 2f).toPx() }
    val leadGap = with(density) { 16.dp.toPx() }
    val minClearance = with(density) { 6.dp.toPx() }

    // 1. 垂直决策：无论单行还是错误态挂框，总卡片底边距准星中心恒定保留 30dp（净空 16dp）
    val vertOffset = with(density) { 30.dp.toPx() }
    val placeAbove = (target.anchor.y - effectiveTotalHeightPx - vertOffset) >= margin
    val popupY = if (placeAbove) {
        target.anchor.y - effectiveTotalHeightPx - vertOffset
    } else {
        target.anchor.y + vertOffset
    }.coerceIn(margin, (containerHeight - effectiveTotalHeightPx - margin).coerceAtLeast(margin))

    // 2. 水平三态决策 (Pure Left, Pure Right, Central Stagger)
    val reticleLeft = target.anchor.x - reticleRadius
    val reticleRight = target.anchor.x + reticleRadius
    val spaceLeft = reticleLeft - margin
    val spaceRight = (containerWidth - margin) - reticleRight
    val requiredSpace = effectiveBarWidthPx + leadGap

    val canFitPureLeft = spaceLeft >= requiredSpace
    val canFitPureRight = spaceRight >= requiredSpace
    val preferLeft = target.anchor.x > containerWidth / 2f

    val isPureLeft = canFitPureLeft && (preferLeft || !canFitPureRight)
    val isPureRight = canFitPureRight && (!preferLeft || !canFitPureLeft)

    val popupX = when {
        isPureLeft -> {
            (reticleLeft - leadGap - effectiveBarWidthPx)
                .coerceIn(margin, (containerWidth - effectiveBarWidthPx - margin).coerceAtLeast(margin))
        }
        isPureRight -> {
            (reticleRight + leadGap)
                .coerceIn(margin, (containerWidth - effectiveBarWidthPx - margin).coerceAtLeast(margin))
        }
        preferLeft -> {
            // 中央走廊（偏左）：靠左边距摆放，给右侧引线腾出广阔舒展空域
            margin
        }
        else -> {
            // 中央走廊（偏右）：靠右边距摆放，给左侧引线腾出广阔舒展空域
            (containerWidth - effectiveBarWidthPx - margin).coerceAtLeast(margin)
        }
    }

    val pixelX = (target.normalized.x * bitmap.width).roundToInt()
        .coerceIn(0, (bitmap.width - 1).coerceAtLeast(0))
    val pixelY = (target.normalized.y * bitmap.height).roundToInt()
        .coerceIn(0, (bitmap.height - 1).coerceAtLeast(0))

    val sendEnabled = clickState == DesktopClickState.Idle || clickState is DesktopClickState.Error

    LaunchedEffect(clickState) {
        if (clickState == DesktopClickState.Sent) {
            delay(350)
            onSent()
        }
    }

    Box(modifier = Modifier.fillMaxSize()) {
        // 1. 半透明暗色背景遮罩：阻断下层触控，点击全屏任意空白区域即直接取消关闭
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.32f))
                .pointerInput(Unit) {
                    detectTapGestures(
                        onTap = { onCancel() },
                        onLongPress = { onCancel() },
                    )
                },
        )

        // 2. 战术正交引线 (Orthogonal HUD Leader Line)：恒定侧壁腰线中轴挺入，严禁逆向穿心
        // 无论当前是否处于错误态，引线与对接端口凸缘恒定保持纯正冷静的战术青色
        Canvas(modifier = Modifier.fillMaxSize()) {
            val lineColor = AgentGridColors.Cyan.copy(alpha = 0.8f)
            val pointColor = AgentGridColors.Cyan
            val strokeWidth = 1.2.dp.toPx()

            val cardLeft = popupX
            val cardRight = popupX + effectiveBarWidthPx
            val cardCenterY = popupY + popupHeightPx / 2f

            // 决定引线扎入哪一侧的侧壁腰线
            val dockAtRight = isPureLeft || (!isPureRight && preferLeft)

            val start: Offset
            val attach: Offset
            val midX: Float

            if (dockAtRight) {
                attach = Offset(cardRight, cardCenterY)
                if (cardRight <= reticleLeft - minClearance) {
                    // 纯左翼：从准星左臂射出，向左折阶梯挺入微条右壁
                    start = Offset(reticleLeft, target.anchor.y)
                    midX = (start.x + attach.x) / 2f
                } else {
                    // 中央走廊偏左：从准星右臂向右射出，绕开准星向外舒展后折入右壁
                    start = Offset(reticleRight, target.anchor.y)
                    midX = (maxOf(reticleRight, cardRight) + leadGap).coerceAtMost(containerWidth - margin)
                }
            } else {
                attach = Offset(cardLeft, cardCenterY)
                if (cardLeft >= reticleRight + minClearance) {
                    // 纯右翼：从准星右臂射出，向右折阶梯挺入微条左壁
                    start = Offset(reticleRight, target.anchor.y)
                    midX = (start.x + attach.x) / 2f
                } else {
                    // 中央走廊偏右：从准星左臂向左射出，绕开准星向外舒展后折入左壁
                    start = Offset(reticleLeft, target.anchor.y)
                    midX = (minOf(reticleLeft, cardLeft) - leadGap).coerceAtLeast(margin)
                }
            }

            // 绘制 3 段 90° 正交折线
            drawLine(color = lineColor, start = start, end = Offset(midX, start.y), strokeWidth = strokeWidth)
            drawLine(color = lineColor, start = Offset(midX, start.y), end = Offset(midX, attach.y), strokeWidth = strokeWidth)
            drawLine(color = lineColor, start = Offset(midX, attach.y), end = attach, strokeWidth = strokeWidth)

            // 侧壁嵌入式战术对接端口凸缘 (Docking Terminal Lug)
            val lugWidthPx = 3.dp.toPx()
            val lugHeightPx = 7.dp.toPx()
            drawRect(
                color = pointColor,
                topLeft = Offset(attach.x - lugWidthPx / 2f, attach.y - lugHeightPx / 2f),
                size = Size(lugWidthPx, lugHeightPx),
            )
        }

        // 3. 十字瞄准准星 (Tactical Reticle)
        Canvas(
            modifier = Modifier
                .offset {
                    IntOffset(
                        (target.anchor.x - with(density) { markerSize.toPx() } / 2f).roundToInt(),
                        (target.anchor.y - with(density) { markerSize.toPx() } / 2f).roundToInt(),
                    )
                }
                .size(markerSize),
        ) {
            val center = Offset(size.width / 2f, size.height / 2f)
            val arm = size.minDimension * 0.46f
            val gapPx = size.minDimension * 0.16f
            val darkStroke = 3.2.dp.toPx()
            val lightStroke = 1.4.dp.toPx()

            fun drawCrossLines(color: Color, stroke: Float) {
                drawLine(color, Offset(center.x - arm, center.y), Offset(center.x - gapPx, center.y), stroke)
                drawLine(color, Offset(center.x + gapPx, center.y), Offset(center.x + arm, center.y), stroke)
                drawLine(color, Offset(center.x, center.y - arm), Offset(center.x, center.y - gapPx), stroke)
                drawLine(color, Offset(center.x, center.y + gapPx), Offset(center.x, center.y + arm), stroke)
            }
            drawCrossLines(Color.Black.copy(alpha = 0.9f), darkStroke)
            drawCrossLines(AgentGridColors.Cyan, lightStroke)

            // 准星中心瞄准微环：保留纯透空孔径，确保物理目标像素 100% 毫无遮挡
            drawCircle(
                color = Color.Black.copy(alpha = 0.85f),
                radius = gapPx * 0.95f,
                center = center,
                style = Stroke(width = 2.4.dp.toPx()),
            )
            drawCircle(
                color = AgentGridColors.Cyan,
                radius = gapPx * 0.95f,
                center = center,
                style = Stroke(width = 1.dp.toPx()),
            )
        }

        // 4. 一体化单行战术微条 (Tactical Inline Bar)：自适应内容宽度（零硬编码），高度 28dp
        Column(
            modifier = Modifier
                .offset { IntOffset(popupX.roundToInt(), popupY.roundToInt()) }
                .onSizeChanged { size ->
                    if (size.height > 0 && totalHeightPx != size.height.toFloat()) {
                        totalHeightPx = size.height.toFloat()
                    }
                }
                .pointerInput(Unit) { detectTapGestures { } },
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Row(
                modifier = Modifier
                    .height(popupHeight)
                    .onSizeChanged { size ->
                        if (size.width > 0 && barWidthPx != size.width.toFloat()) {
                            barWidthPx = size.width.toFloat()
                        }
                    }
                    .background(
                        color = Color(0xF20A1018),
                        shape = RectangleShape,
                    )
                    .border(
                        width = 1.dp,
                        color = AgentGridColors.Cyan.copy(alpha = 0.8f),
                        shape = RectangleShape,
                    )
                    .padding(horizontal = 7.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                // 坐标展示区 + 垂直极细分隔线
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(3.5.dp)
                            .background(
                                AgentGridColors.Cyan,
                                RectangleShape,
                            ),
                    )
                    Text(
                        text = "$pixelX, $pixelY",
                        color = AgentGridColors.Text,
                        fontSize = 9.sp,
                        fontWeight = FontWeight.Bold,
                        letterSpacing = 0.3.sp,
                        maxLines = 1,
                        softWrap = false,
                    )
                    Box(
                        modifier = Modifier
                            .padding(start = 2.dp)
                            .width(1.dp)
                            .height(11.dp)
                            .background(AgentGridColors.Divider),
                    )
                }

                // 终端方括号操作按键组：[ 左键 ] [ 右键 ] [ 双击 ]
                Row(
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    val actions = listOf(
                        "left" to "左键",
                        "right" to "右键",
                        "double" to "双击",
                    )
                    for ((actKey, actLabel) in actions) {
                        val isCurrentError = clickState is DesktopClickState.Error && activeAction == actKey
                        val btnColor = when {
                            clickState == DesktopClickState.Sending -> AgentGridColors.Dimmed
                            isCurrentError -> AgentGridColors.Red
                            else -> AgentGridColors.Cyan
                        }
                        Box(
                            modifier = Modifier
                                .width(42.dp)
                                .clickable(enabled = sendEnabled) {
                                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                    activeAction = actKey
                                    onSend(actKey)
                                }
                                .padding(vertical = 2.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                text = "[ $actLabel ]",
                                color = btnColor,
                                fontSize = 9.5.sp,
                                fontWeight = FontWeight.Bold,
                                letterSpacing = 0.2.sp,
                                maxLines = 1,
                                softWrap = false,
                            )
                        }
                    }
                }
            }

            // 错误详情展示（仅在出错时在微条下方悬挂精炼提示框，宽度严格与微条对其）
            if (clickState is DesktopClickState.Error) {
                Box(
                    modifier = Modifier
                        .width(with(density) { effectiveBarWidthPx.toDp() })
                        .background(Color(0xE6180808), RectangleShape)
                        .border(1.dp, AgentGridColors.Red.copy(alpha = 0.6f), RectangleShape)
                        .padding(horizontal = 6.dp, vertical = 3.dp),
                ) {
                    Text(
                        text = clickState.message,
                        color = AgentGridColors.Red,
                        fontSize = 9.sp,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        lineHeight = 11.sp,
                        softWrap = true,
                    )
                }
            }
        }
    }
}

/**
 * 桌面显示器图标：手画几何线框，保持与任务栏、像素预览相同的极简终端像素感
 */
@Composable
fun DesktopScreenshotIcon(active: Boolean = false) {
    Canvas(modifier = Modifier.size(13.dp)) {
        val unit = size.minDimension / 14f
        val color = if (active) AgentGridColors.Cyan else AgentGridColors.Muted
        val stroke = 1.3f * unit

        // 1. 显示器屏幕框 (上宽下高，稍留内陷)
        drawRect(
            color = color,
            topLeft = Offset(1.5f * unit, 1.5f * unit),
            size = Size(11f * unit, 7.5f * unit),
            style = Stroke(width = stroke),
        )

        // 2. 支架立柱
        drawLine(
            color = color,
            start = Offset(7f * unit, 9f * unit),
            end = Offset(7f * unit, 11.5f * unit),
            strokeWidth = stroke,
        )

        // 3. 底座横梁
        drawLine(
            color = color,
            start = Offset(4f * unit, 11.5f * unit),
            end = Offset(10f * unit, 11.5f * unit),
            strokeWidth = stroke,
        )
    }
}
