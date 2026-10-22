package com.agentgrid.mobile.attachments

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PromptAssemblerTest {
    @Test
    fun `无附件时原文原样返回`() {
        assertEquals("看看这个报错", PromptAssembler.assemble("看看这个报错", emptyList()))
    }

    @Test
    fun `全部就绪时序号与图片条下标一致`() {
        val attachments = listOf(
            PromptAttachment("c1", "a.jpg", localPath = "C:\\a\\1.jpg", state = AttachmentState.READY),
            PromptAttachment("c2", "b.jpg", localPath = "C:\\a\\2.jpg", state = AttachmentState.READY),
        )
        val text = PromptAssembler.assemble("对比[图1]和[图2]", attachments)
        assertEquals(
            "对比[图1]和[图2]\n\n[附图清单]:\n" +
                "- 图1: \"C:\\a\\1.jpg\"\n- 图2: \"C:\\a\\2.jpg\"",
            text,
        )
    }

    @Test
    fun `非就绪兜底不进清单（正常发送路径已被拦下）`() {
        val attachments = listOf(
            PromptAttachment("c1", "a.jpg", state = AttachmentState.UPLOADING),
            PromptAttachment("c2", "b.jpg", localPath = "C:\\a\\1.jpg", state = AttachmentState.READY),
        )
        assertEquals(
            "看看[图1]报错\n\n[附图清单]:\n- 图1: \"C:\\a\\1.jpg\"",
            PromptAssembler.assemble("看看[图1]报错", attachments),
        )
    }
}

class PromptTokensTest {
    @Test
    fun `删中间一张后整体重排`() {
        assertEquals(
            "[图1] [图2]",
            PromptTokens.renumberAfterRemoval("[图1] [图2] [图3]", 2),
        )
    }

    @Test
    fun `删第一张后其余前移`() {
        assertEquals("[图1][图2]", PromptTokens.renumberAfterRemoval("[图1][图2][图3]", 1))
    }

    @Test
    fun `被删编号的占位符去掉且不留双空格`() {
        assertEquals("看看 这个", PromptTokens.renumberAfterRemoval("看看 [图1] 这个", 1))
    }

    @Test
    fun `同一编号出现多次时一并删除`() {
        assertEquals("[图1]", PromptTokens.renumberAfterRemoval("[图1] [图2] [图2]", 2))
    }

    @Test
    fun `没有占位符时原样返回`() {
        assertEquals("普通文本", PromptTokens.renumberAfterRemoval("普通文本", 1))
    }

    @Test
    fun `退格删掉右括号时整块删除`() {
        val result = PromptTokens.expandAtomicDeletion("[图1]", "[图1")
        assertEquals("", result?.text)
        assertEquals(0, result?.cursor)
    }

    @Test
    fun `选区删掉块内字符时整块删除`() {
        val result = PromptTokens.expandAtomicDeletion("看[图12]这里", "看[图1]这里")
        assertEquals("看这里", result?.text)
        assertEquals(1, result?.cursor)
    }

    @Test
    fun `删除占位符左边的空格不触发整块删除`() {
        assertNull(PromptTokens.expandAtomicDeletion("a [图1]", "a[图1]"))
    }

    @Test
    fun `删除占位符右边的字符不触发整块删除`() {
        assertNull(PromptTokens.expandAtomicDeletion("[图1] b", "[图1]b"))
    }

    @Test
    fun `输入文字不触发整块删除`() {
        assertNull(PromptTokens.expandAtomicDeletion("[图1]", "[图1] "))
    }

    @Test
    fun `没有占位符的删除不干预`() {
        assertNull(PromptTokens.expandAtomicDeletion("abcdef", "abcde"))
    }
}
