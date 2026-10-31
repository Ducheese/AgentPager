package com.agentgrid.mobile.domain

/**
 * Mermaid 的 securityLevel 是固定枚举，不是自由字符串。
 * 统一在这里做白名单和回退，避免设置持久化/旧版本写入非法值时把 JS 拼坏。
 */
object MermaidSecurityLevel {
    const val DEFAULT = "strict"

    val VALUES = listOf("strict", "loose", "antiscript")

    fun normalize(value: String?): String =
        value?.takeIf { it in VALUES } ?: DEFAULT

    fun next(current: String): String {
        val index = VALUES.indexOf(normalize(current))
        return VALUES[(index + 1) % VALUES.size]
    }
}
