package com.agentgrid.mobile

import com.agentgrid.mobile.domain.AgentLifecycle
import com.agentgrid.mobile.domain.AgentSource
import com.agentgrid.mobile.domain.TaskSnapshot
import org.junit.Assert.assertEquals
import org.junit.Test

class TaskSnapshotTest {
    @Test
    fun `运行中的任务使用当前时间计算耗时`() {
        val task = task(AgentLifecycle.RUNNING, updatedAt = 2_000)

        assertEquals(4_000, task.elapsedAt(now = 5_000))
    }

    @Test
    fun `完成的任务使用完成时间冻结耗时`() {
        val task = task(
            lifecycle = AgentLifecycle.SUCCEEDED,
            updatedAt = 3_000,
            completedAt = 2_500,
        )

        assertEquals(1_500, task.elapsedAt(now = 9_000))
    }

    @Test
    fun `离线的任务作为终态使用完成时间或更新时间冻结耗时`() {
        val taskWithCompleted = task(
            lifecycle = AgentLifecycle.OFFLINE,
            updatedAt = 3_000,
            completedAt = 2_800,
        )
        assertEquals(1_800, taskWithCompleted.elapsedAt(now = 9_000))

        val taskWithoutCompleted = task(
            lifecycle = AgentLifecycle.OFFLINE,
            updatedAt = 3_000,
            completedAt = null,
        )
        assertEquals(2_000, taskWithoutCompleted.elapsedAt(now = 9_000))
    }

    @Test
    fun `等待审批或回答的任务耗时冻结在更新时间（挂起点）`() {
        val approval = task(
            lifecycle = AgentLifecycle.WAITING_APPROVAL,
            updatedAt = 2_500,
        )
        assertEquals(1_500, approval.elapsedAt(now = 10_000))

        val answer = task(
            lifecycle = AgentLifecycle.WAITING_ANSWER,
            updatedAt = 3_200,
        )
        assertEquals(2_200, answer.elapsedAt(now = 10_000))
    }

    @Test
    fun `空闲状态的任务耗时显示为0`() {
        val idle = task(
            lifecycle = AgentLifecycle.IDLE,
            updatedAt = 3_000,
        )
        assertEquals(0L, idle.elapsedAt(now = 10_000))
    }

    private fun task(
        lifecycle: AgentLifecycle,
        updatedAt: Long,
        completedAt: Long? = null,
    ) = TaskSnapshot(
        id = "task-1",
        source = AgentSource.CODEX_CLI,
        projectName = "AgentGrid",
        lifecycle = lifecycle,
        startedAt = 1_000,
        updatedAt = updatedAt,
        completedAt = completedAt,
    )
}
