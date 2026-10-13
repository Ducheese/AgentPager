package com.agentgrid.mobile

import com.agentgrid.mobile.domain.ControlAction
import com.agentgrid.mobile.domain.ControlPayload
import com.agentgrid.mobile.domain.SignedControlEnvelope
import com.agentgrid.mobile.network.ControlSigner
import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class ControlSignerTest {
    @Test
    fun `签名原文与 Swift 规范一致`() {
        val envelope = SignedControlEnvelope(
            messageId = "6f539e96-6bce-4fdc-94d5-3cf4ea755622",
            sentAt = 1_785_067_200_000,
            deviceId = "nova4",
            sequence = 7u,
            nonce = "nonce-7",
            payload = ControlPayload("task-1", ControlAction.APPROVE),
            signature = "",
        )

        assertEquals(
            """
            1
            6f539e96-6bce-4fdc-94d5-3cf4ea755622
            1785067200000
            nova4
            7
            nonce-7
            control.request
            {"action":"approve","taskID":"task-1"}
            """.trimIndent(),
            ControlSigner.signingText(envelope),
        )
    }

    @Test
    fun `作答与多选题的签名原文形状`() {
        val base = SignedControlEnvelope(
            messageId = "6f539e96-6bce-4fdc-94d5-3cf4ea755622",
            sentAt = 1_785_067_200_000,
            deviceId = "nova4",
            sequence = 7u,
            nonce = "nonce-7",
            payload = ControlPayload("task-1", ControlAction.ANSWER, "就用 A"),
            signature = "",
        )
        // 自由文本：老形状，只有 value
        assertEquals(
            """{"action":"answer","taskID":"task-1","value":"就用 A"}""",
            ControlSigner.signingText(base).lineSequence().last(),
        )
        // 多选题：values **追加在末尾**（桥端 SigningData 按同一顺序拼）
        assertEquals(
            """{"action":"answer","taskID":"task-1","value":"就用 A","values":["A（甲）","B（乙）"]}""",
            ControlSigner.signingText(base.copy(payload = base.payload.copy(values = listOf("A（甲）", "B（乙）"))))
                .lineSequence().last(),
        )
        // 只有选项、没有打字：value 缺席，values 仍是末位
        assertEquals(
            """{"action":"answer","taskID":"task-1","values":["A","B"]}""",
            ControlSigner.signingText(
                base.copy(payload = ControlPayload("task-1", ControlAction.ANSWER, null, listOf("A", "B"))),
            ).lineSequence().last(),
        )
        // 空列表不算（与桥端 `Count > 0` 同口径）
        assertEquals(
            """{"action":"answer","taskID":"task-1","value":"x"}""",
            ControlSigner.signingText(
                base.copy(payload = ControlPayload("task-1", ControlAction.ANSWER, "x", emptyList())),
            ).lineSequence().last(),
        )
    }

    @Test
    fun `相同内容和密钥生成稳定 HMAC`() {
        val secret = ByteArray(32) { 0x42 }
        val envelope = SignedControlEnvelope(
            messageId = "6f539e96-6bce-4fdc-94d5-3cf4ea755622",
            sentAt = 1_785_067_200_000,
            deviceId = "nova4",
            sequence = 7u,
            nonce = "nonce-7",
            payload = ControlPayload("task-1", ControlAction.APPROVE),
            signature = "",
        )

        val first = ControlSigner.sign(envelope, secret)
        val second = ControlSigner.sign(envelope, secret)
        assertEquals(first, second)
        assertEquals(32, Base64.getDecoder().decode(first).size)
        assertNotEquals(first, ControlSigner.sign(envelope.copy(payload = envelope.payload.copy(action = ControlAction.DENY)), secret))
    }
}

