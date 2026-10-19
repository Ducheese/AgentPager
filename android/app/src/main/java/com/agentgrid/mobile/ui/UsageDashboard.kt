package com.agentgrid.mobile.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.SizeTransform
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.agentgrid.mobile.domain.AgentLifecycle
import com.agentgrid.mobile.domain.DailyUsagePoint
import com.agentgrid.mobile.domain.UsageSnapshot
import com.agentgrid.mobile.domain.UsageWindow
import com.agentgrid.mobile.render.PixelCoreSurfaceView
import com.agentgrid.mobile.render.PixelRenderState
import kotlin.math.max
import kotlin.math.roundToInt

private val UsageBar = Color(0xFF326B73)
private val UsageBarDimmed = Color(0xFF1C3940)

@Composable
internal fun UsageDashboard(
    usage: UsageSnapshot?,
    connected: Boolean,
    modifier: Modifier = Modifier,
    usages: List<UsageSnapshot> = emptyList(),
) {
    val availableUsages = remember(usage, usages) {
        val list = usages.ifEmpty { listOfNotNull(usage) }
        list.distinctBy { providerKey(it) }
    }
    // 选择按提供商身份记住，而不是按列表实例：额度每 10 分钟刷新会重建 list，
    // 旧写法 remember(availableUsages) 会把用户踢回第一个提供商。
    var selectedProviderKey by remember { mutableStateOf<String?>(null) }
    val selectedProviderIndex = availableUsages
        .indexOfFirst { providerKey(it) == selectedProviderKey }
        .takeIf { it >= 0 } ?: 0
    val activeUsage = availableUsages.getOrNull(selectedProviderIndex) ?: usage
    var selectedRange by remember { mutableStateOf(UsageRange.SEVEN_DAYS) }
    val allPoints = activeUsage?.dailyUsage.orEmpty()

    BoxWithConstraints(
        modifier = modifier.fillMaxWidth(),
    ) {
        val isCompact = maxWidth < 600.dp || maxHeight > maxWidth
        val contentPadding = if (isCompact) {
            Modifier.padding(horizontal = 16.dp, vertical = 12.dp)
        } else {
            Modifier.padding(horizontal = 36.dp, vertical = 20.dp)
        }

        if (isCompact) {
            // 竖屏、分屏、悬浮小窗：单列垂直流式排布，支持滚动防遮挡
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .then(contentPadding)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(18.dp),
            ) {
                QuotaPanel(
                    usage = activeUsage,
                    availableUsages = availableUsages,
                    selectedUsageIndex = selectedProviderIndex,
                    onSelectUsageIndex = { index ->
                        availableUsages.getOrNull(index)?.let { selectedProviderKey = providerKey(it) }
                    },
                    connected = connected,
                    modifier = Modifier.fillMaxWidth(),
                    compact = true,
                )

                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(1.dp)
                        .background(AgentGridColors.Divider),
                )

                HistoryPanel(
                    allPoints = allPoints,
                    selectedRange = selectedRange,
                    onRangeSelected = { selectedRange = it },
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 180.dp),
                    compact = true,
                )
            }
        } else {
            // 宽屏横屏桌面态：保持经典左右双栏
            val quotaWidth = if (maxWidth < 700.dp) 226.dp else 270.dp
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .then(contentPadding)
                    .height(310.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                QuotaPanel(
                    usage = activeUsage,
                    availableUsages = availableUsages,
                    selectedUsageIndex = selectedProviderIndex,
                    onSelectUsageIndex = { index ->
                        availableUsages.getOrNull(index)?.let { selectedProviderKey = providerKey(it) }
                    },
                    connected = connected,
                    modifier = Modifier
                        .width(quotaWidth)
                        .fillMaxHeight(),
                    compact = false,
                )

                Box(
                    modifier = Modifier
                        .padding(horizontal = 24.dp)
                        .width(1.dp)
                        .fillMaxHeight()
                        .background(AgentGridColors.Divider),
                )

                HistoryPanel(
                    allPoints = allPoints,
                    selectedRange = selectedRange,
                    onRangeSelected = { selectedRange = it },
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxHeight(),
                    compact = false,
                )
            }
        }
    }
}

@Composable
private fun QuotaPanel(
    usage: UsageSnapshot?,
    availableUsages: List<UsageSnapshot>,
    selectedUsageIndex: Int,
    onSelectUsageIndex: (Int) -> Unit,
    connected: Boolean,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
) {
    val isAgy = usage?.planType?.contains("Antigravity", ignoreCase = true) == true
    val windows = usage?.windows.orEmpty()
    val hasThreeP = windows.any { it.key.startsWith("3p") }
    // 分组选择同样不能被额度刷新重置；3P 消失时回到 Gemini，回来不自动跳回 3P
    var selectedGroup by remember { mutableStateOf(0) } // 0 = Gemini, 1 = 3P
    LaunchedEffect(hasThreeP) {
        if (!hasThreeP) selectedGroup = 0
    }

    // 画条的两个窗口：先按 key 取（agy 的 primary/secondary），取不到就退到"前两个要画条的窗口"——
    // 这样新增提供商不用改这里也能显示（Command Code 的 key 就不是 primary/secondary）。
    val barWindows = windows.filter { it.detail == null }
    val primary = if (hasThreeP && selectedGroup == 1) {
        windows.firstOrNull { it.key == "3p-5h" }
    } else {
        windows.firstOrNull { it.key == "primary" } ?: barWindows.getOrNull(0)
    }

    val secondary = if (hasThreeP && selectedGroup == 1) {
        windows.firstOrNull { it.key == "3p-weekly" }
    } else {
        windows.firstOrNull { it.key == "secondary" } ?: barWindows.getOrNull(1)
    }

    // 不带百分比的窗口（如月额度余额）只占一行文字，不画条。
    val noteWindow = windows.firstOrNull { it.detail != null }

    Column(
        modifier = modifier,
        verticalArrangement = if (compact) Arrangement.spacedBy(14.dp) else Arrangement.SpaceBetween,
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            AndroidView(
                factory = { PixelCoreSurfaceView(it) },
                update = {
                    it.updateState(
                        PixelRenderState(
                            lifecycle = if (connected) {
                                AgentLifecycle.IDLE
                            } else {
                                AgentLifecycle.OFFLINE
                            },
                        ),
                    )
                },
                modifier = Modifier
                    .size(42.dp)
                    .semantics {
                        contentDescription = if (connected) {
                            "${providerName(usage)} 空闲"
                        } else {
                            "${providerName(usage)} 离线"
                        }
                    },
            )
            Column(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                BoxWithConstraints(modifier = Modifier.fillMaxWidth()) {
                    // 极窄分屏里标题和三个切换键挤一行，会把 CODEX 逐字母折行、按钮被拉高。
                    // 低于阈值就整组隐藏（要切提供商把窗口拉宽），比折行可用。
                    val showProviderSwitcher =
                        availableUsages.size > 1 && maxWidth >= 170.dp
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            "${providerLabel(usage)} 额度",
                            color = AgentGridColors.Text,
                            fontSize = 14.sp,
                            fontWeight = FontWeight.Bold,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f, fill = false),
                        )
                        if (showProviderSwitcher) {
                            ProviderSwitcher(
                                usages = availableUsages,
                                selectedIndex = selectedUsageIndex,
                                onSelect = onSelectUsageIndex,
                            )
                        }
                    }
                }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        when (providerName(usage)) {
                            // agy 的 limitID 就是订阅名（Google AI Pro）
                            "Antigravity" -> usage?.limitID?.uppercase() ?: "GOOGLE AI PRO"
                            // Command Code 显示成 "COMMAND CODE GO 订阅"（GO 由 planId 推出）
                            "Command Code" -> usage?.limitID?.uppercase()?.let { "$it 订阅" } ?: "订阅计划"
                            else -> usage?.planType?.uppercase()?.let { "$it 计划" } ?: "订阅计划"
                        },
                        color = AgentGridColors.Muted,
                        fontSize = 9.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    if (hasThreeP) {
                        GroupSwitcher(
                            selectedGroup = selectedGroup,
                            onSelectGroup = { selectedGroup = it },
                        )
                    }
                }
            }
        }

        PrimaryQuota(window = primary)

        SecondaryQuota(window = secondary)

        noteWindow?.detail?.let { detail ->
            Text(
                "${noteWindow.label} · $detail",
                color = AgentGridColors.Muted,
                fontSize = 10.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        Text(
            buildString {
                append(UsagePresentation.freshnessText(usage?.capturedAt))
                if (!connected) append(" · Bridge 离线")
            },
            color = if (connected) AgentGridColors.Dimmed else AgentGridColors.Red,
            fontSize = 9.sp,
            maxLines = 1,
        )
    }
}

/** 额度的来源全称：新增提供商只改这里。 */
private fun providerName(usage: UsageSnapshot?): String = when {
    usage?.planType?.contains("Antigravity", ignoreCase = true) == true -> "Antigravity"
    usage?.planType?.contains("Command Code", ignoreCase = true) == true -> "Command Code"
    else -> "Codex"
}

/** 切换器与标题上的短名。 */
private fun providerLabel(usage: UsageSnapshot?): String = when (providerName(usage)) {
    "Antigravity" -> "AGY"
    "Command Code" -> "CMDC"
    else -> "CODEX"
}

@Composable
private fun ProviderSwitcher(
    usages: List<UsageSnapshot>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
) {
    Row(
        modifier = Modifier
            .background(AgentGridColors.SurfaceRaised)
            .padding(1.dp),
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        usages.forEachIndexed { index, u ->
            val label = providerLabel(u)
            val selected = index == selectedIndex
            Box(
                modifier = Modifier
                    .background(if (selected) AgentGridColors.Cyan.copy(alpha = 0.2f) else Color.Transparent)
                    .clickable { onSelect(index) }
                    .padding(horizontal = 6.dp, vertical = 2.dp),
            ) {
                Text(
                    label,
                    color = if (selected) AgentGridColors.Cyan else AgentGridColors.Muted,
                    fontSize = 8.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    softWrap = false,
                )
            }
        }
    }
}

@Composable
private fun GroupSwitcher(
    selectedGroup: Int,
    onSelectGroup: (Int) -> Unit,
) {
    // 和 ProviderSwitcher 一致的一体化微型槽设计：8sp 粗体、外层 SurfaceRaised 底色、选中青色半透底。
    Row(
        modifier = Modifier
            .background(AgentGridColors.SurfaceRaised)
            .padding(1.dp),
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        GroupTab("GEMINI", selectedGroup == 0) { onSelectGroup(0) }
        GroupTab("3P 模型", selectedGroup == 1) { onSelectGroup(1) }
    }
}

@Composable
private fun GroupTab(label: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .background(
                if (selected) AgentGridColors.Cyan.copy(alpha = 0.2f)
                else Color.Transparent,
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    ) {
        Text(
            label,
            color = if (selected) AgentGridColors.Cyan else AgentGridColors.Muted,
            fontSize = 8.sp,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            softWrap = false,
        )
    }
}

@Composable
private fun PrimaryQuota(window: UsageWindow?) {
    val remaining = window?.remainingPercentage?.roundToInt()?.coerceIn(0, 100)
    val color = quotaColor(remaining)
    val label = window?.label?.uppercase() ?: "5H"
    val displayLabel = if (label.endsWith("剩余")) label else "$label 剩余"

    Column(verticalArrangement = Arrangement.spacedBy(7.dp)) {
        Row(
            verticalAlignment = Alignment.Bottom,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(
                remaining?.let { "$it%" } ?: "--%",
                color = color,
                fontSize = 42.sp,
                lineHeight = 42.sp,
                fontWeight = FontWeight.Bold,
            )
            Text(
                displayLabel,
                color = AgentGridColors.Muted,
                fontSize = 10.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(bottom = 5.dp),
            )
        }
        LinearProgressIndicator(
            progress = { ((remaining ?: 0) / 100f).coerceIn(0f, 1f) },
            modifier = Modifier
                .fillMaxWidth()
                .height(5.dp)
                .semantics {
                    contentDescription = "主要额度剩余 ${remaining ?: 0}%"
                },
            color = color,
            trackColor = AgentGridColors.Divider,
            gapSize = 0.dp,
            drawStopIndicator = {},
        )
        Text(
            UsagePresentation.resetText(window?.resetsAt),
            color = AgentGridColors.Muted,
            fontSize = 10.sp,
        )
    }
}

@Composable
private fun SecondaryQuota(window: UsageWindow?) {
    val remaining = window?.remainingPercentage?.roundToInt()?.coerceIn(0, 100)
    val color = quotaColor(remaining)
    val label = window?.label?.uppercase() ?: "7D"
    val displayLabel = if (label.endsWith("额度")) label else "$label 额度"

    Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                displayLabel,
                color = AgentGridColors.Muted,
                fontSize = 9.sp,
            )
            Text(
                remaining?.let { "$it% 剩余" } ?: "--% 剩余",
                color = color,
                fontSize = 12.sp,
                fontWeight = FontWeight.Bold,
            )
        }
        LinearProgressIndicator(
            progress = { ((remaining ?: 0) / 100f).coerceIn(0f, 1f) },
            modifier = Modifier
                .fillMaxWidth()
                .height(3.dp)
                .semantics {
                    contentDescription = "次要额度剩余 ${remaining ?: 0}%"
                },
            color = color,
            trackColor = AgentGridColors.Divider,
            gapSize = 0.dp,
            drawStopIndicator = {},
        )
        Text(
            UsagePresentation.resetText(window?.resetsAt),
            color = AgentGridColors.Dimmed,
            fontSize = 9.sp,
        )
    }
}

@Composable
private fun HistoryPanel(
    allPoints: List<DailyUsagePoint>,
    selectedRange: UsageRange,
    onRangeSelected: (UsageRange) -> Unit,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
) {
    val visiblePoints = remember(allPoints, selectedRange) {
        UsagePresentation.pointsForRange(allPoints, selectedRange)
    }
    val totalTokens = remember(visiblePoints) {
        UsagePresentation.totalTokens(visiblePoints)
    }
    val estimatedCostUSD = remember(visiblePoints) {
        UsagePresentation.totalEstimatedCostUSD(visiblePoints)
    }

    Column(
        modifier = modifier,
        verticalArrangement = if (compact) Arrangement.spacedBy(14.dp) else Arrangement.SpaceBetween,
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.Top,
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(
                    "近期用量",
                    color = AgentGridColors.Text,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                )
                Text(
                    if (estimatedCostUSD == null) {
                        "${selectedRange.days} 天合计"
                    } else {
                        "${selectedRange.days} 天 · API 价预估"
                    },
                    color = AgentGridColors.Muted,
                    fontSize = 9.sp,
                )
            }
            Column(
                horizontalAlignment = Alignment.End,
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                Text(
                    if (estimatedCostUSD == null) {
                        UsagePresentation.compactTokens(totalTokens)
                    } else {
                        UsagePresentation.estimatedCostUSD(estimatedCostUSD)
                    },
                    color = AgentGridColors.Cyan,
                    fontSize = 24.sp,
                    lineHeight = 24.sp,
                    fontWeight = FontWeight.Bold,
                )
                Text(
                    if (estimatedCostUSD == null) {
                        "TOKEN"
                    } else {
                        "${UsagePresentation.compactTokens(totalTokens)} TOKEN"
                    },
                    color = AgentGridColors.Dimmed,
                    fontSize = 8.sp,
                    letterSpacing = 1.sp,
                )
            }
        }

        UsageRangeSelector(
            selectedRange = selectedRange,
            onRangeSelected = onRangeSelected,
            modifier = Modifier.align(Alignment.End),
        )

        AnimatedContent(
            targetState = selectedRange,
            transitionSpec = {
                (
                    fadeIn(animationSpec = androidx.compose.animation.core.tween(150)) togetherWith
                        fadeOut(animationSpec = androidx.compose.animation.core.tween(100))
                    ).using(SizeTransform(clip = false))
            },
            label = "用量时间段切换",
        ) {
            val rangePoints = UsagePresentation.pointsForRange(allPoints, it)
            UsageBars(
                points = rangePoints,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(if (compact) 116.dp else 132.dp),
            )
        }
    }
}

@Composable
private fun UsageRangeSelector(
    selectedRange: UsageRange,
    onRangeSelected: (UsageRange) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier.background(AgentGridColors.Surface),
    ) {
        UsageRange.entries.forEach { range ->
            val selected = range == selectedRange
            Box(
                modifier = Modifier
                    .height(44.dp)
                    .widthIn(min = 48.dp)
                    .background(
                        if (selected) AgentGridColors.SurfaceRaised else Color.Transparent,
                        RectangleShape,
                    )
                    .clickable { onRangeSelected(range) }
                    .semantics {
                        contentDescription = "显示最近 ${range.label}用量"
                    },
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    range.label,
                    color = if (selected) AgentGridColors.Cyan else AgentGridColors.Muted,
                    fontSize = 9.sp,
                    fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
                )
            }
        }
    }
}

@Composable
private fun UsageBars(
    points: List<DailyUsagePoint>,
    modifier: Modifier = Modifier,
) {
    val hasUsage = points.any { it.totalTokens > 0 }
    val density = LocalDensity.current
    val dividerWidth = with(density) { 1.dp.toPx() }
    val smallGap = with(density) { 1.dp.toPx() }
    val mediumGap = with(density) { 2.dp.toPx() }
    val largeGap = with(density) { 6.dp.toPx() }

    Column(modifier = modifier) {
        Box(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
                .semantics {
                    contentDescription = if (hasUsage) {
                        "近期 Token 用量柱状图，共 ${points.size} 天"
                    } else {
                        "暂无近期 Token 用量"
                    }
                },
        ) {
            Canvas(modifier = Modifier.fillMaxSize()) {
                val baselineY = size.height - dividerWidth
                drawRect(
                    color = AgentGridColors.Divider,
                    topLeft = Offset(0f, baselineY),
                    size = Size(size.width, dividerWidth),
                )
                if (!hasUsage || points.isEmpty()) return@Canvas

                val maximum = points.maxOf { it.totalTokens }.coerceAtLeast(1L)
                val slotWidth = size.width / points.size
                val gap = when {
                    points.size <= 7 -> largeGap
                    points.size <= 30 -> mediumGap
                    else -> smallGap
                }
                val barWidth = max(1f, slotWidth - gap)
                points.forEachIndexed { index, point ->
                    if (point.totalTokens <= 0) return@forEachIndexed
                    val ratio = point.totalTokens.toFloat() / maximum
                    val barHeight = max(dividerWidth, baselineY * ratio)
                    val color = when {
                        index == points.lastIndex -> AgentGridColors.Cyan
                        ratio >= 0.5f -> UsageBar
                        else -> UsageBarDimmed
                    }
                    drawRect(
                        color = color,
                        topLeft = Offset(
                            x = index * slotWidth + (slotWidth - barWidth) / 2,
                            y = baselineY - barHeight,
                        ),
                        size = Size(barWidth, barHeight),
                    )
                }
            }

            if (!hasUsage) {
                Text(
                    "暂无近 ${points.size.coerceAtLeast(7)} 天用量",
                    color = AgentGridColors.Dimmed,
                    fontSize = 9.sp,
                    modifier = Modifier.align(Alignment.Center),
                )
            }
        }

        Spacer(Modifier.height(6.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            val first = points.firstOrNull()?.date
            val middle = points.getOrNull(points.lastIndex.coerceAtLeast(0) / 2)?.date
            val last = points.lastOrNull()?.date
            listOf(first, middle, last).forEach { date ->
                Text(
                    date?.let(UsagePresentation::dateLabel) ?: "--/--",
                    color = AgentGridColors.Dimmed,
                    fontSize = 8.sp,
                )
            }
        }
    }
}

private fun quotaColor(remaining: Int?): Color = when {
    remaining == null -> AgentGridColors.Dimmed
    remaining < 10 -> AgentGridColors.Red
    remaining < 20 -> AgentGridColors.Orange
    else -> AgentGridColors.Cyan
}

/** 提供商的稳定身份：跨越额度刷新仍是同一个值，选择状态才不会被重建的列表冲掉。 */
private fun providerKey(usage: UsageSnapshot): String =
    usage.planType ?: usage.limitID ?: ""
