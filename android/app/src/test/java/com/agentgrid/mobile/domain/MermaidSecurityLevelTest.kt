package com.agentgrid.mobile.domain

import org.junit.Assert.assertEquals
import org.junit.Test

class MermaidSecurityLevelTest {
    @Test
    fun `未知等级回退到 strict`() {
        assertEquals("strict", MermaidSecurityLevel.normalize(null))
        assertEquals("strict", MermaidSecurityLevel.normalize(""))
        assertEquals("strict", MermaidSecurityLevel.normalize("sandbox"))
    }

    @Test
    fun `合法等级原样保留`() {
        assertEquals("strict", MermaidSecurityLevel.normalize("strict"))
        assertEquals("loose", MermaidSecurityLevel.normalize("loose"))
        assertEquals("antiscript", MermaidSecurityLevel.normalize("antiscript"))
    }

    @Test
    fun `按钮循环 strict 到 loose 到 antiscript`() {
        assertEquals("loose", MermaidSecurityLevel.next("strict"))
        assertEquals("antiscript", MermaidSecurityLevel.next("loose"))
        assertEquals("strict", MermaidSecurityLevel.next("antiscript"))
    }

    @Test
    fun `非法当前值循环从默认值开始`() {
        assertEquals("loose", MermaidSecurityLevel.next("garbage"))
    }
}
