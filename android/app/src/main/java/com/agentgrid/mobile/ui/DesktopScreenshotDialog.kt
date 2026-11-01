package com.agentgrid.mobile.ui

import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.agentgrid.mobile.DesktopScreenshotState
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 桌面全屏截图对话框：
 * - 顶部工具栏：时间戳、[ 刷新 ]、[ 关闭 ]
 * - 画布区域：双指缩放拖动、双击缩放切换、系统返回键退出
 */
@Composable
fun DesktopScreenshotDialog(
    state: DesktopScreenshotState,
    onRefresh: () -> Unit,
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
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = "DESKTOP · 屏幕截图",
                        color = AgentGridColors.Cyan,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                    )
                    if (state is DesktopScreenshotState.Success) {
                        val timeStr = remember(state.capturedAt) {
                            SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(Date(state.capturedAt))
                        }
                        Text(
                            text = timeStr,
                            color = AgentGridColors.Muted,
                            fontSize = 11.sp,
                        )
                    }
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(14.dp),
                ) {
                    Text(
                        text = "[ 刷新 ]",
                        color = AgentGridColors.Cyan,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier.clickable { onRefresh() },
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
                        DesktopScreenshotCanvas(bitmap = state.bitmap)
                    }
                }
            }
        }
    }
}

/**
 * 具有平滑双指捏合缩放、拖动画布平移、双击智能切换缩放比例的图片展示区域
 */
@Composable
private fun DesktopScreenshotCanvas(bitmap: Bitmap) {
    val imageBitmap = remember(bitmap) { bitmap.asImageBitmap() }
    var scale by remember(bitmap) { mutableFloatStateOf(1f) }
    var offset by remember(bitmap) { mutableStateOf(Offset.Zero) }

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

        LaunchedEffect(containerWidth, containerHeight) {
            scale = 1f
            offset = Offset.Zero
        }

        val transformableState = rememberTransformableState { zoomChange, panChange, _ ->
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
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .transformable(state = transformableState)
                .pointerInput(bitmap) {
                    detectTapGestures(
                        onDoubleTap = { tapOffset ->
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
