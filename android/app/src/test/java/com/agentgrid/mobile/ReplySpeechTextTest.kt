package com.agentgrid.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ReplySpeechTextTest {
    @Test
    fun `代码块和 mermaid 不进入朗读文本`() {
        val markdown = """
            # 标题

            这是正文。

            ```kotlin
            val x = 1
            ```

            ```mermaid
            flowchart LR
              A --> B
            ```
        """.trimIndent()

        assertEquals("标题\n这是正文。", ReplySpeechText.speakable(markdown))
    }

    @Test
    fun `行内标记与链接被剥掉`() {
        val text = ReplySpeechText.speakable("看 **重点** 和 [文档](https://example.com) 以及 `代码`！")

        assertEquals("看 重点 和 文档 以及 代码！", text)
    }

    @Test
    fun `列表项剥掉任务标记带出正文`() {
        val markdown = """
            - [x] 已完成的事
            - [ ] 待办的事
            1. 第一项
        """.trimIndent()

        assertEquals("已完成的事\n待办的事\n第一项", ReplySpeechText.speakable(markdown))
    }

    @Test
    fun `表格按行连读`() {
        val markdown = """
            | 名称 | 状态 |
            | --- | --- |
            | 构建 | 通过 |
        """.trimIndent()

        assertEquals("名称，状态\n构建，通过", ReplySpeechText.speakable(markdown))
    }

    @Test
    fun `长文本按句子分片且单片不超限`() {
        val sentence = "这是一句用来测试分片的中文句子。"
        val text = buildString { repeat(300) { append(sentence) } }

        val chunks = ReplySpeechText.chunk(text, limit = 1_000)

        assertTrue(chunks.size >= 5)
        assertTrue(chunks.all { it.length <= 1_000 })
        assertTrue(chunks.none { it.isBlank() })
        // 全是完整句子，分片拼接后一字不差、一句不碎
        assertEquals(text, chunks.joinToString(""))
    }

    @Test
    fun `单句超长时硬切保证不超限`() {
        val text = "长".repeat(2_500)

        val chunks = ReplySpeechText.chunk(text, limit = 1_000)

        assertEquals(3, chunks.size)
        assertTrue(chunks.all { it.length <= 1_000 })
        assertEquals(text, chunks.joinToString(""))
    }
}
