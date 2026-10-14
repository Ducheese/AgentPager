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
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
        list.distinctBy { it.planType ?: it.limitID }
    }
    var selectedProviderIndex by remember(availableUsages) { mutableStateOf(0) }
    val activeUsage = availableUsages.getOrNull(selectedProviderIndex) ?: usage
    var selectedRange by remember { mutableStateOf(UsageRange.SEVEN_DAYS) }
    val allPoints = activeUsage?.dailyUsage.orEmpty()

    BoxWithConstraints(
        modifier = modifier
            .fillMaxWidth()
            .height(310.dp)
            .padding(horizontal = 36.dp, vertical = 20.dp),
    ) {
        val quotaWidth = if (maxWidth < 700.dp) 226.dp else 270.dp

        Row(
            modifier = Modifier.fillMaxSize(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            QuotaPanel(
                usage = activeUsage,
                availableUsages = availableUsages,
                selectedUsageIndex = selectedProviderIndex,
                onSelectUsageIndex = { selectedProviderIndex = it },
                connected = connected,
                modifier = Modifier
                    .width(quotaWidth)
                    .fillMaxHeight(),
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
            )
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
) {
    val isAgy = usage?.planType?.contains("Antigravity", ignoreCase = true) == true
    val windows = usage?.windows.orEmpty()
    val hasThreeP = windows.any { it.key.startsWith("3p") }
    var selectedGroup by remember(usage) { mutableStateOf(0) } // 0 = Gemini, 1 = 3P

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
        verticalArrangement = Arrangement.SpaceBetween,
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
                    )
                    if (availableUsages.size > 1) {
                        ProviderSwitcher(
                            usages = availableUsages,
                            selectedIndex = selectedUsageIndex,
                            onSelect = onSelectUsageIndex,
                        )
                    }
                }
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
                )
            }
        }

        if (hasThreeP) {
            GroupSwitcher(
                selectedGroup = selectedGroup,
                onSelectGroup = { selectedGroup = it },
            )
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
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(AgentGridColors.SurfaceRaised)
            .padding(2.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        val geminiSelected = selectedGroup == 0
        val threePSelected = selectedGroup == 1
        Box(
            modifier = Modifier
                .weight(1f)
                .background(if (geminiSelected) AgentGridColors.Surface else Color.Transparent)
                .clickable { onSelectGroup(0) }
                .padding(vertical = 3.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                "GEMINI",
                color = if (geminiSelected) AgentGridColors.Cyan else AgentGridColors.Muted,
                fontSize = 9.sp,
                fontWeight = if (geminiSelected) FontWeight.Bold else FontWeight.Normal,
            )
        }
        Box(
            modifier = Modifier
                .weight(1f)
                .background(if (threePSelected) AgentGridColors.Surface else Color.Transparent)
                .clickable { onSelectGroup(1) }
                .padding(vertical = 3.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                "3P 模型",
                color = if (threePSelected) AgentGridColors.Cyan else AgentGridColors.Muted,
                fontSize = 9.sp,
                fontWeight = if (threePSelected) FontWeight.Bold else FontWeight.Normal,
            )
        }
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
        verticalArrangement = Arrangement.SpaceBetween,
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
                    .height(132.dp),
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
