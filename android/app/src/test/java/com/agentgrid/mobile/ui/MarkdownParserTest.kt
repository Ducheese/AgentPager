package com.agentgrid.mobile.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class MarkdownParserTest {

    @Test
    fun `标准表格与列对齐能正确解析`() {
        val markdown = """
            | Column A | Column B | Column C |
            | :--- | :---: | ---: |
            | Apple | 10 | High |
            | Banana | 20 | Low |
        """.trimIndent()

        val blocks = MarkdownParser.parse(markdown)
        assertEquals(1, blocks.size)
        assertTrue(blocks[0] is MarkdownBlock.Table)

        val table = blocks[0] as MarkdownBlock.Table
        assertEquals(listOf("Column A", "Column B", "Column C"), table.headers)
        assertEquals(
            listOf(TableAlignment.LEFT, TableAlignment.CENTER, TableAlignment.RIGHT),
            table.alignments,
        )
        assertEquals(2, table.rows.size)
        assertEquals(listOf("Apple", "10", "High"), table.rows[0])
        assertEquals(listOf("Banana", "20", "Low"), table.rows[1])
    }

    @Test
    fun `省略首尾管道符的表格也能正确解析`() {
        val markdown = """
            Name | Age | Role
            --- | --- | ---
            Alice | 25 | Dev
            Bob | 30 | QA
        """.trimIndent()

        val blocks = MarkdownParser.parse(markdown)
        assertEquals(1, blocks.size)
        assertTrue(blocks[0] is MarkdownBlock.Table)

        val table = blocks[0] as MarkdownBlock.Table
        assertEquals(listOf("Name", "Age", "Role"), table.headers)
        assertEquals(2, table.rows.size)
        assertEquals(listOf("Alice", "25", "Dev"), table.rows[0])
    }

    @Test
    fun `代码块与混合段落能正确区分`() {
        val markdown = """
            这是前面的一段普通介绍。

            ```kotlin
            fun main() {
                println("Hello AgentPager!")
            }
            ```

            这是后面的总结。
        """.trimIndent()

        val blocks = MarkdownParser.parse(markdown)
        assertEquals(3, blocks.size)
        assertTrue(blocks[0] is MarkdownBlock.Paragraph)
        assertTrue(blocks[1] is MarkdownBlock.CodeBlock)
        assertTrue(blocks[2] is MarkdownBlock.Paragraph)

        val code = blocks[1] as MarkdownBlock.CodeBlock
        assertEquals("kotlin", code.language)
        assertEquals("fun main() {\n    println(\"Hello AgentPager!\")\n}", code.content)
    }

    @Test
    fun `标题与列表能够正确解析`() {
        val markdown = """
            ## 核心目标
            - 目标 1
            - 目标 2

            ### 实施步骤
            1. 第一步
            2. 第二步
        """.trimIndent()

        val blocks = MarkdownParser.parse(markdown)
        assertEquals(4, blocks.size)

        val h2 = blocks[0] as MarkdownBlock.Heading
        assertEquals(2, h2.level)
        assertEquals("核心目标", h2.text)

        val bullet = blocks[1] as MarkdownBlock.BulletList
        assertEquals(
            listOf(
                MarkdownBlock.BulletItem(level = 0, text = "目标 1"),
                MarkdownBlock.BulletItem(level = 0, text = "目标 2"),
            ),
            bullet.items,
        )

        val h3 = blocks[2] as MarkdownBlock.Heading
        assertEquals(3, h3.level)
        assertEquals("实施步骤", h3.text)

        val ordered = blocks[3] as MarkdownBlock.OrderedList
        assertEquals(
            listOf(
                MarkdownBlock.OrderedItem(level = 0, number = 1, text = "第一步"),
                MarkdownBlock.OrderedItem(level = 0, number = 2, text = "第二步"),
            ),
            ordered.items,
        )
    }

    @Test
    fun `富文本内联标记解析为带样式的字符串`() {
        val text = "这是 **粗体文字** 和 `val x = 42` 代码，以及普通的 *斜体* 说明"
        val annotated = MarkdownParser.buildInlineMarkdown(text)
        assertTrue(annotated.text.contains("粗体文字"))
        assertTrue(annotated.text.contains("val x = 42"))
        assertTrue(annotated.text.contains("斜体"))
        assertEquals(3, annotated.spanStyles.size)
    }

    @Test
    fun `粗斜体下划线删除线与剧透能够正确解析样式`() {
        val text = "***粗斜体***、___下划线粗斜体___、__下划线__、~~删除线~~、||剧透||"
        val annotated = MarkdownParser.buildInlineMarkdown(text)
        assertTrue(annotated.text.contains("粗斜体"))
        assertTrue(annotated.text.contains("下划线粗斜体"))
        assertTrue(annotated.text.contains("下划线"))
        assertTrue(annotated.text.contains("删除线"))
        assertTrue(annotated.text.contains("剧透"))
        // 粗斜体(BOLD+ITALIC 2个) + 下划线粗斜体(UNDERLINE+ITALIC 2个) + 下划线(1个) + 删除线(1个) + 剧透(1个) = 7个
        assertEquals(7, annotated.spanStyles.size)
    }

    @Test
    fun `粗体内部嵌套代码与斜体能够正确支持`() {
        val text = "**粗体里带 `代码` 和 *斜体***"
        val annotated = MarkdownParser.buildInlineMarkdown(text)
        assertEquals("粗体里带  代码  和 斜体", annotated.text)
        assertEquals(3, annotated.spanStyles.size)

        // 验证外层粗体覆盖全长
        val boldSpan = annotated.spanStyles.find { it.item.fontWeight == androidx.compose.ui.text.font.FontWeight.Bold }
        assertTrue(boldSpan != null)
        assertEquals(0, boldSpan!!.start)
        assertEquals(annotated.text.length, boldSpan.end)

        // 验证内联代码样式
        val codeSpan = annotated.spanStyles.find { it.item.fontFamily == androidx.compose.ui.text.font.FontFamily.Monospace }
        assertTrue(codeSpan != null)
        val codeContent = annotated.text.substring(codeSpan!!.start, codeSpan.end)
        assertEquals(" 代码 ", codeContent)

        // 验证内层斜体覆盖末尾
        val italicSpan = annotated.spanStyles.find { it.item.fontStyle == androidx.compose.ui.text.font.FontStyle.Italic }
        assertTrue(italicSpan != null)
        val italicContent = annotated.text.substring(italicSpan!!.start, italicSpan.end)
        assertEquals("斜体", italicContent)
    }

    @Test
    fun `GFM任务待办列表正确解析为任务项`() {
        val completed = MarkdownParser.parseTaskItem("[x] 已完成：读取配置")
        assertTrue(completed.isTask)
        assertTrue(completed.isChecked)
        assertEquals("已完成：读取配置", completed.text)

        val completedUpper = MarkdownParser.parseTaskItem("[X] 编译通过")
        assertTrue(completedUpper.isTask)
        assertTrue(completedUpper.isChecked)
        assertEquals("编译通过", completedUpper.text)

        val pending = MarkdownParser.parseTaskItem("[ ] 未完成：端到端测试")
        assertTrue(pending.isTask)
        assertTrue(!pending.isChecked)
        assertEquals("未完成：端到端测试", pending.text)

        val normal = MarkdownParser.parseTaskItem("普通水果：苹果")
        assertTrue(!normal.isTask)
        assertTrue(!normal.isChecked)
        assertEquals("普通水果：苹果", normal.text)
    }

    @Test
    fun `多级嵌套列表与段落缩进能够正确解析`() {
        val markdown = """
            这是顶格正文。
                这是缩进 4 格的正文。

            - 顶层项目 A
              - 二级子项目 A-1
                - 三级子项目 A-1-a
            - 顶层项目 B

            1. 第一步
               1. 子步骤 1.1
               2. 子步骤 1.2
            2. 第二步
        """.trimIndent()

        val blocks = MarkdownParser.parse(markdown)
        assertEquals(3, blocks.size)

        // 验证段落包含缩进
        assertTrue(blocks[0] is MarkdownBlock.Paragraph)
        val para = blocks[0] as MarkdownBlock.Paragraph
        assertTrue(para.text.contains("这是顶格正文。\n    这是缩进 4 格的正文。"))

        // 验证无序列表层级
        assertTrue(blocks[1] is MarkdownBlock.BulletList)
        val bullet = blocks[1] as MarkdownBlock.BulletList
        assertEquals(4, bullet.items.size)
        assertEquals(0, bullet.items[0].level)
        assertEquals("顶层项目 A", bullet.items[0].text)
        assertEquals(1, bullet.items[1].level)
        assertEquals("二级子项目 A-1", bullet.items[1].text)
        assertEquals(2, bullet.items[2].level)
        assertEquals("三级子项目 A-1-a", bullet.items[2].text)
        assertEquals(0, bullet.items[3].level)
        assertEquals("顶层项目 B", bullet.items[3].text)

        // 验证有序列表层级
        assertTrue(blocks[2] is MarkdownBlock.OrderedList)
        val ordered = blocks[2] as MarkdownBlock.OrderedList
        assertEquals(4, ordered.items.size)
        assertEquals(0, ordered.items[0].level)
        assertEquals(1, ordered.items[0].number)
        assertEquals(1, ordered.items[1].level)
        assertEquals(1, ordered.items[1].number)
        assertEquals(1, ordered.items[2].level)
        assertEquals(2, ordered.items[2].number)
        assertEquals(0, ordered.items[3].level)
        assertEquals(2, ordered.items[3].number)
    }
}
