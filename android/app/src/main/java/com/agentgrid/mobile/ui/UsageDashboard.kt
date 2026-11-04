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
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
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
    hideCodexQuota: Boolean = false,
    hideCmdcQuota: Boolean = false,
) {
    val availableUsages = remember(usage, usages, hideCodexQuota, hideCmdcQuota) {
        availableProviderUsages(usage, usages, hideCodexQuota, hideCmdcQuota)
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
    // 分组选择上提到这里：compact/wide 是两个 QuotaPanel 实例，旋转跨阈值会重建，
    // 状态放里面就丢（竖屏切"全部"再转横屏被踢回"当前"）。放外面，旋转不丢。
    var selectedGroup by remember { mutableStateOf(0) } // AGY: 0 = Gemini, 1 = 3P
    var selectedCmdcView by remember { mutableStateOf(0) } // CMDC: 0 = 当前，1 = 全部
    // 多账号列表只在 CMDC 激活时有值：过滤出子账号（LimitID 是邮箱，含 @），
    // 提升到外层避免两处 QuotaPanel 重复计算。
    val cmdcAccounts = remember(activeUsage, usages) {
        if (providerName(activeUsage) == "Command Code") {
            usages
                .filter { providerName(it) == "Command Code" && it.limitID?.contains('@') == true }
                .distinctBy { it.limitID.orEmpty() }
        } else {
            emptyList()
        }
    }

    BoxWithConstraints(
        modifier = modifier.fillMaxWidth(),
    ) {
        if (availableUsages.isEmpty()) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 40.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = "所有额度面板已在设置中隐藏",
                    color = AgentGridColors.Muted,
                    fontSize = 12.sp,
                )
            }
            return@BoxWithConstraints
        }

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
                    selectedGroup = selectedGroup,
                    onSelectGroup = { selectedGroup = it },
                    selectedCmdcView = selectedCmdcView,
                    onSelectCmdcView = { selectedCmdcView = it },
                    cmdcAccounts = cmdcAccounts,
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
                    selectedGroup = selectedGroup,
                    onSelectGroup = { selectedGroup = it },
                    selectedCmdcView = selectedCmdcView,
                    onSelectCmdcView = { selectedCmdcView = it },
                    cmdcAccounts = cmdcAccounts,
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
    selectedGroup: Int,
    onSelectGroup: (Int) -> Unit,
    selectedCmdcView: Int,
    onSelectCmdcView: (Int) -> Unit,
    cmdcAccounts: List<UsageSnapshot>,
    connected: Boolean,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
) {
    val isAgy = usage?.planType?.contains("Antigravity", ignoreCase = true) == true
    val isCmdc = usage?.planType?.contains("Command Code", ignoreCase = true) == true
    // OpenCode Go 三个窗口都是百分比（5 小时 / 周 / 月），所以比另外两家多画一根细条。
    val isOpenCodeGo = usage?.planType?.contains("OpenCode Go", ignoreCase = true) == true
    // 横屏定高 310dp：百分比/进度条区域可能放不下（OCG 三根、或字体放大时两家也会超）。
    // 只滚这块内容，标题和 footer 固定；竖屏由外层滚动容器接管，不要套两层。
    val quotaScroll = rememberScrollState()
    val windows = usage?.windows.orEmpty()
    // 切提供商 / GEMINI↔3P / CMDC 当前↔全部 时回到顶部。
    LaunchedEffect(usage?.planType, selectedGroup, selectedCmdcView) {
        quotaScroll.scrollTo(0)
    }
    val hasThreeP = windows.any { it.key.startsWith("3p") }
    // 自动收回监听：3P 没了回 GEMINI，单/零 CMDC 号时回"当前"
    // 注意：跨刷新的状态保持靠 remember 天然完成，这两个 effect 只管"收回"，不管"保持"。
    LaunchedEffect(isAgy, hasThreeP) {
        if (isAgy && !hasThreeP) onSelectGroup(0)
    }
    LaunchedEffect(isCmdc, cmdcAccounts.size) {
        if (isCmdc && cmdcAccounts.isEmpty()) onSelectCmdcView(0)
    }

    // 画条按提供商自己的槽位取；未知就留占位（空条 + --% + 重置时间未知），
    // 不允许拿别的窗口顶替，避免把周额度画成 5 小时或重复画月额度。
    val barWindows = windows.filter { it.detail == null }
    val primary: UsageWindow?
    val secondary: UsageWindow?
    val primaryLabel: String
    val secondaryLabel: String
    when {
        isOpenCodeGo -> {
            primary = windows.firstOrNull { it.key == "oc-rolling" }
            secondary = windows.firstOrNull { it.key == "oc-weekly" }
            primaryLabel = "5 小时"
            secondaryLabel = "周额度"
        }
        isCmdc -> {
            primary = windows.firstOrNull { it.key == "cc-5h" }
            secondary = windows.firstOrNull { it.key == "cc-weekly" }
            primaryLabel = "5 小时"
            secondaryLabel = "周额度"
        }
        isAgy && hasThreeP && selectedGroup == 1 -> {
            primary = windows.firstOrNull { it.key == "3p-5h" }
            secondary = windows.firstOrNull { it.key == "3p-weekly" }
            primaryLabel = "Claude/GPT 5H"
            secondaryLabel = "Claude/GPT 周额度"
        }
        isAgy -> {
            primary = windows.firstOrNull { it.key == "primary" }
            secondary = windows.firstOrNull { it.key == "secondary" }
            primaryLabel = "Gemini 5H"
            secondaryLabel = "Gemini 周额度"
        }
        else -> {
            // Codex 仍保留原来的通用回退；它的未知态不照搬。
            primary = windows.firstOrNull { it.key == "primary" } ?: barWindows.getOrNull(0)
            secondary = windows.firstOrNull { it.key == "secondary" } ?: barWindows.getOrNull(1)
            primaryLabel = "5H"
            secondaryLabel = "7D"
        }
    }
    val openCodeGoMonthly = windows.firstOrNull { it.key == "oc-monthly" }

    // 不带百分比的窗口（如月额度余额）只占一行文字，不画条。
    val noteWindow = windows.firstOrNull { it.detail != null }
    val showCmdcAll = isCmdc && selectedCmdcView == 1 && cmdcAccounts.isNotEmpty()

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
                            // OpenCode Go 的 usage 接口不返回套餐档位，LimitID 固定为产品名
                            "OpenCode Go" -> usage?.limitID?.uppercase()?.let { "$it 订阅" } ?: "OPENCODE GO 订阅"
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
                            tabs = listOf("GEMINI", "3P 模型"),
                            selectedGroup = selectedGroup,
                            onSelectGroup = onSelectGroup,
                        )
                    }
                    if (isCmdc && cmdcAccounts.isNotEmpty()) {
                        GroupSwitcher(
                            tabs = listOf("当前", "全部"),
                            selectedGroup = selectedCmdcView,
                            onSelectGroup = onSelectCmdcView,
                        )
                    }
                }
            }
        }

        // 所有子面板共用同一个内容滚动区：标题和 footer 固定，只滚中间的百分比/进度条/
        // 账号列表。CMDC「全部」不再单独套内滚，避免双层滚动。
        Column(
            modifier = if (compact) {
                Modifier
            } else {
                Modifier
                    .weight(1f)
                    .verticalScroll(quotaScroll)
            },
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            if (showCmdcAll) {
                // 全部：多号从上到下，一行一号（邮箱 + 周% · MM-dd · 余额月重置），纯文字不画条。
                CmdcAccountsPanel(
                    accounts = cmdcAccounts,
                    modifier = Modifier.padding(top = 10.dp, bottom = 6.dp),
                )
            } else {
                PrimaryQuota(window = primary, fallbackLabel = primaryLabel)

                SecondaryQuota(window = secondary, fallbackLabel = secondaryLabel)

                if (isOpenCodeGo) {
                    SecondaryQuota(window = openCodeGoMonthly, fallbackLabel = "月额度")
                }

                when {
                    noteWindow?.detail != null -> Text(
                        "${noteWindow.label} · ${noteWindow.detail}",
                        color = AgentGridColors.Muted,
                        fontSize = 10.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    isCmdc -> Text(
                        "月额度 · 余额未知 · 重置时间未知",
                        color = AgentGridColors.Muted,
                        fontSize = 10.sp,
                    )
                }
            }
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

/** 额度的来源全称：新增提供商时，同步修改 AgentGridScreen.effectiveUsage 的分类。 */
internal fun providerName(usage: UsageSnapshot?): String = when {
    usage?.planType?.contains("Antigravity", ignoreCase = true) == true -> "Antigravity"
    usage?.planType?.contains("Command Code", ignoreCase = true) == true -> "Command Code"
    usage?.planType?.contains("OpenCode Go", ignoreCase = true) == true -> "OpenCode Go"
    else -> "Codex"
}

/** 切换器与标题上的短名。 */
internal fun providerLabel(usage: UsageSnapshot?): String = when (providerName(usage)) {
    "Antigravity" -> "AGY"
    "Command Code" -> "CMDC"
    "OpenCode Go" -> "OCG"
    else -> "CODEX"
}

/**
 * CMDC 全部面板：多号卡片式双行展示（方案 A）：
 * 第 1 行：账号前缀（高亮加粗）+ 域名（次要小字） | 周用量剩余（状态色加粗）
 * 第 2 行：周重置日期 | 月度余额及重置日
 * 紧凑微型卡片，彻底根除横屏单行文本截断问题。
 */
@Composable
private fun CmdcAccountsPanel(
    accounts: List<UsageSnapshot>,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier,
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        accounts.forEach { account ->
            val weekly = account.windows.firstOrNull { it.key == "cc-weekly" }
            val monthly = account.windows.firstOrNull { it.key == "cc-monthly" }
            val remaining = weekly?.remainingPercentage?.roundToInt()?.coerceIn(0, 100)
            val badgeColor = quotaColor(remaining)

            val email = account.limitID.orEmpty()
            val user = email.substringBefore("@")
            val domain = if (email.contains("@")) "@" + email.substringAfter("@") else ""

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(AgentGridColors.SurfaceRaised)
                    .padding(horizontal = 8.dp, vertical = 6.dp),
                verticalArrangement = Arrangement.spacedBy(3.dp),
            ) {
                // 第 1 行：账号名（左）+ 周用量剩余（右）
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = buildAnnotatedString {
                            withStyle(
                                SpanStyle(
                                    color = AgentGridColors.Text,
                                    fontWeight = FontWeight.Bold,
                                    fontSize = 11.sp,
                                ),
                            ) {
                                append(user)
                            }
                            if (domain.isNotEmpty()) {
                                withStyle(
                                    SpanStyle(
                                        color = AgentGridColors.Dimmed,
                                        fontSize = 9.sp,
                                    ),
                                ) {
                                    append(domain)
                                }
                            }
                        },
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text(
                        text = remaining?.let { "$it%" } ?: "--%",
                        color = badgeColor,
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Bold,
                        maxLines = 1,
                    )
                }

                // 第 2 行：周重置（左）+ 月度余额/重置（右）
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = "周重置 ${UsagePresentation.shortDate(weekly?.resetsAt)}",
                        color = AgentGridColors.Dimmed,
                        fontSize = 9.sp,
                        maxLines = 1,
                    )
                    if (monthly?.detail != null) {
                        Text(
                            text = monthly.detail,
                            color = AgentGridColors.Muted,
                            fontSize = 9.sp,
                            maxLines = 1,
                        )
                    }
                }
            }
        }
    }
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
    tabs: List<String>,
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
        tabs.forEachIndexed { index, label ->
            GroupTab(label, selectedGroup == index) { onSelectGroup(index) }
        }
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
private fun PrimaryQuota(window: UsageWindow?, fallbackLabel: String = "5H") {
    val remaining = window?.remainingPercentage?.roundToInt()?.coerceIn(0, 100)
    val color = quotaColor(remaining)
    val label = window?.label?.uppercase() ?: fallbackLabel.uppercase()
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
                    contentDescription = remaining?.let { "主要额度剩余 $it%" } ?: "主要额度未知"
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
private fun SecondaryQuota(window: UsageWindow?, fallbackLabel: String = "7D") {
    val remaining = window?.remainingPercentage?.roundToInt()?.coerceIn(0, 100)
    val color = quotaColor(remaining)
    val label = window?.label?.uppercase() ?: fallbackLabel.uppercase()
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
                    contentDescription = remaining?.let { "次要额度剩余 $it%" } ?: "次要额度未知"
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
internal fun providerKey(usage: UsageSnapshot): String =
    usage.planType ?: usage.limitID ?: ""

/**
 * 额度面板与状态栏轮播共用的提供商列表：桌面号过滤、去重、隐藏开关，口径必须一致；
 * 新增 provider 时只改这里，两处同步生效。
 */
internal fun availableProviderUsages(
    usage: UsageSnapshot?,
    usages: List<UsageSnapshot>,
    hideCodexQuota: Boolean,
    hideCmdcQuota: Boolean,
): List<UsageSnapshot> {
    val list = usages.ifEmpty { listOfNotNull(usage) }
    return list
        // 桌面号只活在"全部"列表里，不进提供商切换：它们的 LimitID 是邮箱（含 @），
        // 不是套餐名。放进来会冒充主号（distinctBy 按 planType 压成一个 CMDC 代表）。
        .filterNot {
            providerName(it) == "Command Code" &&
                it.limitID?.contains('@') == true
        }
        .distinctBy { providerKey(it) }
        .filterNot { hideCodexQuota && providerLabel(it) == "CODEX" }
        .filterNot { hideCmdcQuota && providerLabel(it) == "CMDC" }
}

/**
 * 状态栏轮播用的进度条：(显示标签, 窗口)，与 QuotaPanel 的槽位规则同口径。
 * - AGY 只取 Gemini 组（group 0），不跟 3P 切换；
 * - CMDC 只取当前视图（cc-5h/cc-weekly），不跟全部账号；
 * - OCG 三根全要（5 小时/周/月）；
 * - 缺失的槽位直接跳过不占位，不拿别的窗口顶替。
 */
internal fun providerStatusBars(usage: UsageSnapshot): List<Pair<String, UsageWindow>> {
    val windows = usage.windows
    val isAgy = usage.planType?.contains("Antigravity", ignoreCase = true) == true
    val isCmdc = usage.planType?.contains("Command Code", ignoreCase = true) == true
    val isOpenCodeGo = usage.planType?.contains("OpenCode Go", ignoreCase = true) == true
    val slots: List<Pair<String, String>> = when {
        isOpenCodeGo -> listOf(
            "OpenCode 5小时" to "oc-rolling",
            "周" to "oc-weekly",
            "月" to "oc-monthly",
        )
        isCmdc -> listOf(
            "CommandCode 5小时" to "cc-5h",
            "周" to "cc-weekly",
        )
        isAgy -> listOf(
            "Gemini 5小时" to "primary",
            "周" to "secondary",
        )
        else -> listOf(
            "Codex 5小时" to "primary",
            "周" to "secondary",
        )
    }
    return slots.mapNotNull { (label, key) ->
        windows.firstOrNull { it.key == key }?.let { label to it }
    }
}
