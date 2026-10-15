package com.agentgrid.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

sealed interface MarkdownBlock {
    data class Heading(val level: Int, val text: String) : MarkdownBlock
    data class Paragraph(val text: String) : MarkdownBlock
    data class CodeBlock(val language: String, val content: String) : MarkdownBlock
    data class Table(
        val headers: List<String>,
        val alignments: List<TableAlignment>,
        val rows: List<List<String>>,
    ) : MarkdownBlock
    data class BulletItem(val level: Int, val text: String)
    data class OrderedItem(val level: Int, val number: Int, val text: String)

    data class BulletList(val items: List<BulletItem>) : MarkdownBlock
    data class OrderedList(val items: List<OrderedItem>) : MarkdownBlock
    data class Blockquote(val lines: List<String>) : MarkdownBlock
    data object HorizontalRule : MarkdownBlock
}

enum class TableAlignment {
    LEFT,
    CENTER,
    RIGHT,
}

object MarkdownParser {
    enum class InlineStyle {
        BOLD,
        ITALIC,
        UNDERLINE,
        STRIKE,
        SPOILER,
    }

    sealed interface InlineToken {
        data class Text(val text: String) : InlineToken
        data class Code(val content: String) : InlineToken
        data class Link(val label: String, val url: String) : InlineToken
        class Delim(
            val char: Char,
            val totalCount: Int,
            val canOpen: Boolean,
            val canClose: Boolean,
        ) : InlineToken {
            var remainingCount: Int = totalCount
        }
    }

    private data class OpenerEntry(
        val tokenIndex: Int,
        val char: Char,
        val count: Int,
        val style: InlineStyle,
    )

    private data class MatchedSpan(
        val openerTokenIndex: Int,
        val closerTokenIndex: Int,
        val style: InlineStyle,
    )

    private val bulletItemRegex = Regex("^([*\\-+•▪])\\s+(.*)")
    private val orderedItemRegex = Regex("^(\\d+)\\.\\s+(.*)")

    data class TaskItemInfo(val isTask: Boolean, val isChecked: Boolean, val text: String)

    fun parseTaskItem(rawItem: String): TaskItemInfo {
        val trimmed = rawItem.trimStart()
        return when {
            trimmed.startsWith("[x] ", ignoreCase = true) ->
                TaskItemInfo(isTask = true, isChecked = true, text = trimmed.substring(4).trim())
            trimmed.startsWith("[x]", ignoreCase = true) ->
                TaskItemInfo(isTask = true, isChecked = true, text = trimmed.substring(3).trim())
            trimmed.startsWith("[ ] ") ->
                TaskItemInfo(isTask = true, isChecked = false, text = trimmed.substring(4).trim())
            trimmed.startsWith("[ ]") ->
                TaskItemInfo(isTask = true, isChecked = false, text = trimmed.substring(3).trim())
            else ->
                TaskItemInfo(isTask = false, isChecked = false, text = rawItem)
        }
    }

    fun parse(markdown: String): List<MarkdownBlock> {
        val rawLines = markdown.replace("\r\n", "\n").replace("\r", "\n").lines()
        val blocks = mutableListOf<MarkdownBlock>()
        var i = 0

        while (i < rawLines.size) {
            val line = rawLines[i]
            val trimmed = line.trim()

            if (trimmed.isEmpty()) {
                i++
                continue
            }

            // 1. 代码块
            if (trimmed.startsWith("```")) {
                val lang = trimmed.removePrefix("```").trim()
                val codeLines = mutableListOf<String>()
                var nextIdx = i + 1
                while (nextIdx < rawLines.size) {
                    val nextLine = rawLines[nextIdx]
                    if (nextLine.trim().startsWith("```")) {
                        nextIdx++
                        break
                    }
                    codeLines.add(nextLine)
                    nextIdx++
                }
                blocks.add(MarkdownBlock.CodeBlock(lang, codeLines.joinToString("\n")))
                i = nextIdx
                continue
            }

            // 2. 表格 (当前行包含 | 且下一行是表格分隔线)
            if (isTableRowCandidate(line) && i + 1 < rawLines.size && isTableSeparator(rawLines[i + 1])) {
                val headers = splitTableRow(line)
                val alignments = parseAlignments(rawLines[i + 1], headers.size)
                val rows = mutableListOf<List<String>>()
                var nextIdx = i + 2
                while (nextIdx < rawLines.size) {
                    val nextLine = rawLines[nextIdx]
                    if (nextLine.trim().isEmpty() || !isTableRowCandidate(nextLine)) {
                        break
                    }
                    rows.add(splitTableRow(nextLine))
                    nextIdx++
                }
                blocks.add(MarkdownBlock.Table(headers, alignments, rows))
                i = nextIdx
                continue
            }

            // 3. 引用块
            if (trimmed.startsWith(">")) {
                val quoteLines = mutableListOf<String>()
                var nextIdx = i
                while (nextIdx < rawLines.size) {
                    val nextLine = rawLines[nextIdx]
                    val nextTrim = nextLine.trim()
                    if (!nextTrim.startsWith(">")) {
                        break
                    }
                    quoteLines.add(nextTrim.removePrefix(">").trimStart())
                    nextIdx++
                }
                blocks.add(MarkdownBlock.Blockquote(quoteLines))
                i = nextIdx
                continue
            }

            // 4. 水平分割线
            if (trimmed.length >= 3 && (trimmed.all { it == '-' } || trimmed.all { it == '*' } || trimmed.all { it == '_' })) {
                blocks.add(MarkdownBlock.HorizontalRule)
                i++
                continue
            }

            // 5. 标题
            if (trimmed.startsWith("#")) {
                var level = 0
                while (level < trimmed.length && trimmed[level] == '#' && level < 6) {
                    level++
                }
                if (level in 1..6 && (level == trimmed.length || trimmed[level].isWhitespace())) {
                    val headingText = trimmed.substring(level).trim()
                    blocks.add(MarkdownBlock.Heading(level, headingText))
                    i++
                    continue
                }
            }

            // 6. 无序列表
            val bulletMatch = bulletItemRegex.find(trimmed)
            if (bulletMatch != null) {
                val items = mutableListOf<MarkdownBlock.BulletItem>()
                var nextIdx = i
                while (nextIdx < rawLines.size) {
                    val rawLine = rawLines[nextIdx]
                    if (rawLine.trim().isEmpty()) break
                    val trimmedLine = rawLine.trimStart()
                    val match = bulletItemRegex.find(trimmedLine) ?: break
                    val leadingSpaces = rawLine.length - trimmedLine.length
                    val level = (leadingSpaces / 2).coerceIn(0, 6)
                    items.add(MarkdownBlock.BulletItem(level = level, text = match.groupValues[2]))
                    nextIdx++
                }
                blocks.add(MarkdownBlock.BulletList(items))
                i = nextIdx
                continue
            }

            // 7. 有序列表
            val orderedMatch = orderedItemRegex.find(trimmed)
            if (orderedMatch != null) {
                val items = mutableListOf<MarkdownBlock.OrderedItem>()
                var nextIdx = i
                while (nextIdx < rawLines.size) {
                    val rawLine = rawLines[nextIdx]
                    if (rawLine.trim().isEmpty()) break
                    val trimmedLine = rawLine.trimStart()
                    val match = orderedItemRegex.find(trimmedLine) ?: break
                    val leadingSpaces = rawLine.length - trimmedLine.length
                    val level = (leadingSpaces / 2).coerceIn(0, 6)
                    val num = match.groupValues[1].toIntOrNull() ?: (items.size + 1)
                    items.add(MarkdownBlock.OrderedItem(level = level, number = num, text = match.groupValues[2]))
                    nextIdx++
                }
                blocks.add(MarkdownBlock.OrderedList(items))
                i = nextIdx
                continue
            }

            // 8. 普通段落
            val paragraphLines = mutableListOf<String>()
            var nextIdx = i
            while (nextIdx < rawLines.size) {
                val nextLine = rawLines[nextIdx]
                val nextTrim = nextLine.trim()
                if (nextTrim.isEmpty() ||
                    nextTrim.startsWith("```") ||
                    (isTableRowCandidate(nextTrim) && nextIdx + 1 < rawLines.size && isTableSeparator(rawLines[nextIdx + 1])) ||
                    nextTrim.startsWith(">") ||
                    nextTrim.startsWith("#") ||
                    bulletItemRegex.containsMatchIn(nextTrim) ||
                    orderedItemRegex.containsMatchIn(nextTrim) ||
                    (nextTrim.length >= 3 && (nextTrim.all { it == '-' } || nextTrim.all { it == '*' } || nextTrim.all { it == '_' }))
                ) {
                    break
                }
                paragraphLines.add(nextLine.trimEnd())
                nextIdx++
            }
            if (paragraphLines.isNotEmpty()) {
                blocks.add(MarkdownBlock.Paragraph(paragraphLines.joinToString("\n")))
            }
            i = nextIdx
        }

        return blocks
    }

    fun isTableRowCandidate(line: String): Boolean {
        return line.contains('|')
    }

    fun isTableSeparator(line: String): Boolean {
        val trimmed = line.trim()
        if (!trimmed.contains('-')) return false
        val cleaned = trimmed.replace("|", "").replace(":", "").replace("-", "").replace(" ", "").replace("\t", "")
        return cleaned.isEmpty()
    }

    fun splitTableRow(line: String): List<String> {
        var trimmed = line.trim()
        if (trimmed.startsWith("|")) {
            trimmed = trimmed.substring(1)
        }
        if (trimmed.endsWith("|")) {
            trimmed = trimmed.substring(0, trimmed.length - 1)
        }
        return trimmed.split("|").map { it.trim() }
    }

    fun parseAlignments(separatorLine: String, columnCount: Int): List<TableAlignment> {
        val cols = splitTableRow(separatorLine)
        return (0 until columnCount).map { i ->
            val col = cols.getOrNull(i)?.trim() ?: ""
            when {
                col.startsWith(":") && col.endsWith(":") -> TableAlignment.CENTER
                col.endsWith(":") -> TableAlignment.RIGHT
                else -> TableAlignment.LEFT
            }
        }
    }

    fun tokenizeInline(text: String): List<InlineToken> {
        val tokens = mutableListOf<InlineToken>()
        var i = 0
        while (i < text.length) {
            // 1. 内联代码 `...`
            if (text[i] == '`') {
                val endIdx = text.indexOf('`', i + 1)
                if (endIdx != -1) {
                    tokens.add(InlineToken.Code(text.substring(i + 1, endIdx)))
                    i = endIdx + 1
                    continue
                }
            }

            // 2. 超链接 [label](url)
            if (text[i] == '[') {
                val closeBracket = text.indexOf(']', i + 1)
                if (closeBracket != -1 && closeBracket + 1 < text.length && text[closeBracket + 1] == '(') {
                    val closeParen = text.indexOf(')', closeBracket + 2)
                    if (closeParen != -1 && !text.substring(i, closeParen).contains('\n')) {
                        val label = text.substring(i + 1, closeBracket)
                        val url = text.substring(closeBracket + 2, closeParen)
                        tokens.add(InlineToken.Link(label, url))
                        i = closeParen + 1
                        continue
                    }
                }
            }

            // 3. 定界符 *, _, ~, |
            val c = text[i]
            if (c == '*' || c == '_' || c == '~' || c == '|') {
                var count = 0
                while (i + count < text.length && text[i + count] == c) {
                    count++
                }

                val prevChar = if (i > 0) text[i - 1] else ' '
                val nextChar = if (i + count < text.length) text[i + count] else ' '
                val canOpen = !nextChar.isWhitespace()
                val canClose = !prevChar.isWhitespace()

                tokens.add(InlineToken.Delim(c, count, canOpen, canClose))
                i += count
                continue
            }

            // 4. 普通文本
            var end = i
            while (end < text.length && text[end] !in "`[*_~|") {
                end++
            }
            tokens.add(InlineToken.Text(text.substring(i, end)))
            i = end
        }
        return tokens
    }

    private fun matchDelimiters(tokens: List<InlineToken>): List<MatchedSpan> {
        val openerStack = mutableListOf<OpenerEntry>()
        val matchedSpans = mutableListOf<MatchedSpan>()

        tokens.forEachIndexed { tokenIdx, token ->
            if (token !is InlineToken.Delim) return@forEachIndexed

            if (token.canClose) {
                var rem = token.remainingCount
                while (rem > 0) {
                    var foundIdx = -1
                    for (s in openerStack.indices.reversed()) {
                        val entry = openerStack[s]
                        if (entry.char == token.char && entry.count <= rem) {
                            foundIdx = s
                            break
                        }
                    }

                    if (foundIdx == -1) break

                    val entry = openerStack.removeAt(foundIdx)
                    rem -= entry.count
                    val openerToken = tokens[entry.tokenIndex] as? InlineToken.Delim
                    openerToken?.let { it.remainingCount -= entry.count }
                    matchedSpans.add(
                        MatchedSpan(
                            openerTokenIndex = entry.tokenIndex,
                            closerTokenIndex = tokenIdx,
                            style = entry.style,
                        ),
                    )
                }
                token.remainingCount = rem
            }

            if (token.canOpen && token.remainingCount > 0) {
                val c = token.char
                val rem = token.remainingCount
                when (c) {
                    '*' -> {
                        if (rem >= 3) {
                            openerStack.add(OpenerEntry(tokenIdx, '*', 2, InlineStyle.BOLD))
                            openerStack.add(OpenerEntry(tokenIdx, '*', 1, InlineStyle.ITALIC))
                        } else if (rem == 2) {
                            openerStack.add(OpenerEntry(tokenIdx, '*', 2, InlineStyle.BOLD))
                        } else if (rem == 1) {
                            openerStack.add(OpenerEntry(tokenIdx, '*', 1, InlineStyle.ITALIC))
                        }
                    }
                    '_' -> {
                        if (rem >= 3) {
                            openerStack.add(OpenerEntry(tokenIdx, '_', 2, InlineStyle.UNDERLINE))
                            openerStack.add(OpenerEntry(tokenIdx, '_', 1, InlineStyle.ITALIC))
                        } else if (rem == 2) {
                            openerStack.add(OpenerEntry(tokenIdx, '_', 2, InlineStyle.UNDERLINE))
                        } else if (rem == 1) {
                            openerStack.add(OpenerEntry(tokenIdx, '_', 1, InlineStyle.ITALIC))
                        }
                    }
                    '~' -> {
                        if (rem >= 2) {
                            openerStack.add(OpenerEntry(tokenIdx, '~', 2, InlineStyle.STRIKE))
                        }
                    }
                    '|' -> {
                        if (rem >= 2) {
                            openerStack.add(OpenerEntry(tokenIdx, '|', 2, InlineStyle.SPOILER))
                        }
                    }
                }
            }
        }

        return matchedSpans
    }

    fun buildInlineMarkdown(text: String): AnnotatedString {
        if (text.isEmpty()) return AnnotatedString("")

        val tokens = tokenizeInline(text)
        val matchedSpans = matchDelimiters(tokens)

        val outputText = StringBuilder()
        val tokenStartOffsets = IntArray(tokens.size)
        val tokenEndOffsets = IntArray(tokens.size)

        data class PendingStyle(val style: SpanStyle, val start: Int, val end: Int)
        val pendingStyles = mutableListOf<PendingStyle>()

        tokens.forEachIndexed { idx, token ->
            tokenStartOffsets[idx] = outputText.length
            when (token) {
                is InlineToken.Text -> {
                    outputText.append(token.text)
                }
                is InlineToken.Code -> {
                    val start = outputText.length
                    outputText.append(" ${token.content} ")
                    pendingStyles.add(
                        PendingStyle(
                            SpanStyle(
                                fontFamily = FontFamily.Monospace,
                                color = AgentGridColors.Cyan,
                                background = AgentGridColors.SurfaceRaised,
                            ),
                            start,
                            outputText.length,
                        ),
                    )
                }
                is InlineToken.Link -> {
                    val start = outputText.length
                    outputText.append(token.label)
                    pendingStyles.add(
                        PendingStyle(
                            SpanStyle(
                                color = AgentGridColors.Blue,
                                textDecoration = TextDecoration.Underline,
                            ),
                            start,
                            outputText.length,
                        ),
                    )
                }
                is InlineToken.Delim -> {
                    if (token.remainingCount > 0) {
                        for (k in 0 until token.remainingCount) {
                            outputText.append(token.char)
                        }
                    }
                }
            }
            tokenEndOffsets[idx] = outputText.length
        }

        matchedSpans.forEach { span ->
            val start = tokenEndOffsets[span.openerTokenIndex]
            val end = tokenStartOffsets[span.closerTokenIndex]
            if (start < end) {
                val spanStyle = when (span.style) {
                    InlineStyle.BOLD -> SpanStyle(fontWeight = FontWeight.Bold, color = AgentGridColors.Text)
                    InlineStyle.ITALIC -> SpanStyle(fontStyle = FontStyle.Italic, color = AgentGridColors.Text)
                    InlineStyle.UNDERLINE -> SpanStyle(textDecoration = TextDecoration.Underline, color = AgentGridColors.Text)
                    InlineStyle.STRIKE -> SpanStyle(textDecoration = TextDecoration.LineThrough, color = AgentGridColors.Muted)
                    InlineStyle.SPOILER -> SpanStyle(background = AgentGridColors.SurfaceRaised, color = AgentGridColors.Muted)
                }
                pendingStyles.add(PendingStyle(spanStyle, start, end))
            }
        }

        val builder = AnnotatedString.Builder(outputText.toString())
        pendingStyles.forEach { p ->
            builder.addStyle(p.style, p.start, p.end)
        }
        return builder.toAnnotatedString()
    }
}

@Composable
fun MarkdownView(
    markdown: String,
    modifier: Modifier = Modifier,
) {
    val blocks = remember(markdown) { MarkdownParser.parse(markdown) }

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        blocks.forEach { block ->
            when (block) {
                is MarkdownBlock.Heading -> HeadingBlockView(block)
                is MarkdownBlock.Paragraph -> ParagraphBlockView(block)
                is MarkdownBlock.CodeBlock -> CodeBlockView(block)
                is MarkdownBlock.Table -> TableBlockView(block)
                is MarkdownBlock.BulletList -> BulletListView(block)
                is MarkdownBlock.OrderedList -> OrderedListView(block)
                is MarkdownBlock.Blockquote -> BlockquoteView(block)
                is MarkdownBlock.HorizontalRule -> HorizontalRuleView()
            }
        }
    }
}

@Composable
private fun HeadingBlockView(block: MarkdownBlock.Heading) {
    val (fontSize, color) = when (block.level) {
        1 -> 13.sp to AgentGridColors.Text
        2 -> 12.sp to AgentGridColors.Cyan
        3 -> 11.sp to AgentGridColors.Yellow
        else -> 10.sp to AgentGridColors.Text
    }
    Text(
        text = MarkdownParser.buildInlineMarkdown(block.text),
        fontSize = fontSize,
        fontWeight = FontWeight.Bold,
        color = color,
        modifier = Modifier.padding(top = 2.dp, bottom = 2.dp),
    )
}

@Composable
private fun ParagraphBlockView(block: MarkdownBlock.Paragraph) {
    Text(
        text = MarkdownParser.buildInlineMarkdown(block.text),
        color = AgentGridColors.Text.copy(alpha = 0.92f),
        fontSize = 10.sp,
        lineHeight = 15.sp,
    )
}

@Composable
private fun CodeBlockView(block: MarkdownBlock.CodeBlock) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, AgentGridColors.Divider)
            .background(AgentGridColors.Background)
            .padding(8.dp),
    ) {
        if (block.language.isNotBlank()) {
            Text(
                text = block.language.uppercase(),
                color = AgentGridColors.Muted,
                fontSize = 8.sp,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.padding(bottom = 4.dp),
            )
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
        ) {
            Text(
                text = block.content,
                color = AgentGridColors.Green,
                fontSize = 10.sp,
                lineHeight = 14.sp,
                fontFamily = FontFamily.Monospace,
            )
        }
    }
}

@Composable
private fun TableBlockView(block: MarkdownBlock.Table) {
    val columnCount = maxOf(block.headers.size, block.rows.maxOfOrNull { it.size } ?: 0)
    if (columnCount == 0) return

    val colWidths = remember(block.headers, block.rows) {
        (0 until columnCount).map { colIndex ->
            val headerText = block.headers.getOrNull(colIndex) ?: ""
            val maxCellLength = maxOf(
                headerText.length,
                block.rows.maxOfOrNull { (it.getOrNull(colIndex) ?: "").length } ?: 0,
            )
            // 字符宽度估算：每个字符按 8.5dp 估算 + 内边距，区间限在 [68dp, 280dp]
            (maxCellLength * 8.5f + 24f).coerceIn(68f, 280f).dp
        }
    }

    val totalWidth = colWidths.fold(0.dp) { sum, width -> sum + width }

    // 边框和宽度都必须落在 Column 自己身上，不能挂在外层滚动容器上。
    // `horizontalScroll` 会把内容测得的宽度约束设成**无穷大**，而无穷大约束下 `fillMaxWidth()`
    // 解析成 constraints.minWidth（= 0）：挂在滚动容器外层的 border 就成了"整块面板宽"的大框，
    // Column 里那些 fillMaxWidth() 的表头线/行间线宽度则直接塌成 0、整条不可见。
    // 给 Column 一个显式宽度，等于把约束重新变回有界，fillMaxWidth() 才按表格自己的宽度解析；
    // 表格比面板窄时边框贴着表格，比面板宽时整体横向滚动。
    //
    // 注：这处是读 Compose 语义发现的独立缺陷，**不是** 2026-10-15 那次"表格散架"的成因
    // （那次是正文换行被 Normalize 抽干，表格压根没走到这里）。修它是因为换行修好之后表格
    // 真的会走到这个分支，缺陷从不可达变成可达。
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
    ) {
        Column(
            modifier = Modifier
                .width(totalWidth)
                .border(1.dp, AgentGridColors.Divider),
        ) {
            // 表头
            Row(modifier = Modifier.background(AgentGridColors.SurfaceRaised)) {
                for (c in 0 until columnCount) {
                    TableCell(
                        text = block.headers.getOrNull(c) ?: "",
                        width = colWidths[c],
                        alignment = block.alignments.getOrNull(c) ?: TableAlignment.LEFT,
                        isHeader = true,
                        showRightBorder = c < columnCount - 1,
                    )
                }
            }

            // 表头分割线
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(1.dp)
                    .background(AgentGridColors.Divider),
            )

            // 数据行
            block.rows.forEachIndexed { rowIndex, row ->
                val bg = if (rowIndex % 2 == 1) {
                    AgentGridColors.Surface.copy(alpha = 0.5f)
                } else {
                    Color.Transparent
                }
                Row(modifier = Modifier.background(bg)) {
                    for (c in 0 until columnCount) {
                        TableCell(
                            text = row.getOrNull(c) ?: "",
                            width = colWidths[c],
                            alignment = block.alignments.getOrNull(c) ?: TableAlignment.LEFT,
                            isHeader = false,
                            showRightBorder = c < columnCount - 1,
                        )
                    }
                }
                if (rowIndex < block.rows.lastIndex) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(0.5.dp)
                            .background(AgentGridColors.Divider.copy(alpha = 0.5f)),
                    )
                }
            }
        }
    }
}

@Composable
private fun TableCell(
    text: String,
    width: Dp,
    alignment: TableAlignment,
    isHeader: Boolean,
    showRightBorder: Boolean,
) {
    val textAlign = when (alignment) {
        TableAlignment.CENTER -> TextAlign.Center
        TableAlignment.RIGHT -> TextAlign.End
        TableAlignment.LEFT -> TextAlign.Start
    }
    val contentAlignment = when (alignment) {
        TableAlignment.CENTER -> Alignment.Center
        TableAlignment.RIGHT -> Alignment.CenterEnd
        TableAlignment.LEFT -> Alignment.CenterStart
    }

    Box(
        modifier = Modifier
            .width(width)
            .then(
                if (showRightBorder) {
                    Modifier.drawBehind {
                        drawLine(
                            color = AgentGridColors.Divider,
                            start = Offset(size.width, 0f),
                            end = Offset(size.width, size.height),
                            strokeWidth = 1f,
                        )
                    }
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 8.dp, vertical = 6.dp),
        contentAlignment = contentAlignment,
    ) {
        Text(
            text = MarkdownParser.buildInlineMarkdown(text),
            color = if (isHeader) AgentGridColors.Cyan else AgentGridColors.Text,
            fontSize = 10.sp,
            fontWeight = if (isHeader) FontWeight.Bold else FontWeight.Normal,
            textAlign = textAlign,
            lineHeight = 14.sp,
        )
    }
}

@Composable
private fun BulletListView(block: MarkdownBlock.BulletList) {
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        block.items.forEach { item ->
            val taskInfo = MarkdownParser.parseTaskItem(item.text)
            val startPadding = (4 + item.level * 12).dp
            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.Top,
                modifier = Modifier.padding(start = startPadding),
            ) {
                if (taskInfo.isTask) {
                    if (taskInfo.isChecked) {
                        Text(
                            text = "☑",
                            color = AgentGridColors.Cyan,
                            fontSize = 10.sp,
                            lineHeight = 14.sp,
                            fontWeight = FontWeight.Bold,
                        )
                    } else {
                        Text(
                            text = "☐",
                            color = AgentGridColors.Muted,
                            fontSize = 10.sp,
                            lineHeight = 14.sp,
                        )
                    }
                    Text(
                        text = MarkdownParser.buildInlineMarkdown(taskInfo.text),
                        color = if (taskInfo.isChecked) AgentGridColors.Text.copy(alpha = 0.85f) else AgentGridColors.Text,
                        fontSize = 10.sp,
                        lineHeight = 14.sp,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                } else {
                    val bulletChar = when (item.level % 3) {
                        0 -> "▪"
                        1 -> "▫"
                        else -> "▸"
                    }
                    Text(
                        text = bulletChar,
                        color = if (item.level == 0) AgentGridColors.Cyan else AgentGridColors.Amber,
                        fontSize = 10.sp,
                        lineHeight = 14.sp,
                    )
                    Text(
                        text = MarkdownParser.buildInlineMarkdown(item.text),
                        color = AgentGridColors.Text,
                        fontSize = 10.sp,
                        lineHeight = 14.sp,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                }
            }
        }
    }
}

@Composable
private fun OrderedListView(block: MarkdownBlock.OrderedList) {
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        block.items.forEach { item ->
            val startPadding = (4 + item.level * 12).dp
            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.Top,
                modifier = Modifier.padding(start = startPadding),
            ) {
                Text(
                    text = "${item.number}.",
                    color = if (item.level == 0) AgentGridColors.Yellow else AgentGridColors.Amber,
                    fontSize = 10.sp,
                    lineHeight = 14.sp,
                )
                Text(
                    text = MarkdownParser.buildInlineMarkdown(item.text),
                    color = AgentGridColors.Text,
                    fontSize = 10.sp,
                    lineHeight = 14.sp,
                    modifier = Modifier.weight(1f, fill = false),
                )
            }
        }
    }
}

@Composable
private fun BlockquoteView(block: MarkdownBlock.Blockquote) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(AgentGridColors.SurfaceRaised.copy(alpha = 0.5f))
            .padding(vertical = 4.dp),
    ) {
        Box(
            modifier = Modifier
                .width(3.dp)
                .background(AgentGridColors.Cyan),
        )
        Spacer(modifier = Modifier.width(8.dp))
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            block.lines.forEach { line ->
                Text(
                    text = MarkdownParser.buildInlineMarkdown(line),
                    color = AgentGridColors.Muted,
                    fontSize = 10.sp,
                    lineHeight = 14.sp,
                )
            }
        }
    }
}

@Composable
private fun HorizontalRuleView() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp)
            .height(1.dp)
            .background(AgentGridColors.Divider),
    )
}
