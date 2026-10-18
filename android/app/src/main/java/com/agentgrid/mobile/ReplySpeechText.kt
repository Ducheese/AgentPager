package com.agentgrid.mobile

import com.agentgrid.mobile.ui.MarkdownBlock
import com.agentgrid.mobile.ui.MarkdownParser

/**
 * REPLY 朗读的纯文本层：把 markdown 正文洗成适合念的句子，再按 TTS 的输入上限分片。
 * 全部是纯函数，无 Android 依赖，单元测试直接跑。
 */
object ReplySpeechText {
    /** TextToSpeech.getMaxSpeechInputLength() 是 4000，留出余量。 */
    const val MAX_CHUNK_LENGTH = 3_500

    private val imageRegex = Regex("!\\[([^\\]]*)\\]\\([^)]*\\)")
    private val linkRegex = Regex("\\[([^\\]]*)\\]\\([^)]*\\)")
    private val codeSpanRegex = Regex("`([^`]*)`")
    private val emphasisRegex = Regex("[*_~`|]")
    private val horizontalSpaceRegex = Regex("[ \\t]+")

    fun speakable(markdown: String): String {
        if (markdown.isBlank()) return ""
        val lines = mutableListOf<String>()
        MarkdownParser.parse(markdown).forEach { block ->
            when (block) {
                is MarkdownBlock.Heading -> lines += stripInline(block.text)
                is MarkdownBlock.Paragraph -> lines += stripInline(block.text)
                is MarkdownBlock.BulletList -> block.items.forEach { item ->
                    lines += stripInline(MarkdownParser.parseTaskItem(item.text).text)
                }
                is MarkdownBlock.OrderedList -> block.items.forEach { item ->
                    lines += stripInline(item.text)
                }
                is MarkdownBlock.Blockquote -> block.lines.forEach { lines += stripInline(it) }
                is MarkdownBlock.Table -> {
                    if (block.headers.isNotEmpty()) {
                        lines += block.headers.joinToString("，") { stripInline(it) }
                    }
                    block.rows.forEach { row ->
                        lines += row.joinToString("，") { stripInline(it) }
                    }
                }
                // 代码块（含 mermaid）不朗读：念代码只会变成噪音
                is MarkdownBlock.CodeBlock -> Unit
                MarkdownBlock.HorizontalRule -> Unit
            }
        }
        return lines
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .joinToString("\n")
    }

    fun chunk(text: String, limit: Int = MAX_CHUNK_LENGTH): List<String> {
        require(limit > 0) { "limit 必须为正数" }
        if (text.length <= limit) return listOf(text)

        val sentences = mutableListOf<String>()
        var start = 0
        text.forEachIndexed { index, char ->
            if (char == '\n' || char in "。！？；!?;") {
                sentences += text.substring(start, index + 1)
                start = index + 1
            }
        }
        if (start < text.length) sentences += text.substring(start)

        val chunks = mutableListOf<String>()
        val builder = StringBuilder()
        fun flush() {
            val piece = builder.toString().trim()
            if (piece.isNotEmpty()) chunks += piece
            builder.setLength(0)
        }
        sentences.forEach { sentence ->
            var remainder = sentence
            while (remainder.length > limit) {
                flush()
                chunks += remainder.substring(0, limit)
                remainder = remainder.substring(limit)
            }
            if (builder.length + remainder.length > limit) flush()
            builder.append(remainder)
        }
        flush()
        return chunks
    }

    private fun stripInline(text: String): String {
        var result = imageRegex.replace(text) { it.groupValues[1] }
        result = linkRegex.replace(result) { it.groupValues[1] }
        result = codeSpanRegex.replace(result) { it.groupValues[1] }
        result = emphasisRegex.replace(result, "")
        result = horizontalSpaceRegex.replace(result, " ")
        return result.trim()
    }
}
