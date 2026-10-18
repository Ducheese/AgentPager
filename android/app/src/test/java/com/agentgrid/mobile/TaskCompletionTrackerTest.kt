package com.agentgrid.mobile

import com.agentgrid.mobile.domain.AgentLifecycle
import com.agentgrid.mobile.domain.AgentSource
import com.agentgrid.mobile.domain.TaskSnapshot
import org.junit.Assert.assertEquals
import org.junit.Test

class TaskCompletionTrackerTest {
    @Test
    fun `非终态跳到终态会上报`() {
        val tracker = TaskCompletionTracker()
        tracker.collect(listOf(task("a", AgentLifecycle.RUNNING)))

        val completed = tracker.collect(
            listOf(
                task("a", AgentLifecycle.SUCCEEDED),
                task("b", AgentLifecycle.RUNNING),
            ),
        )

        assertEquals(listOf("a"), completed.map { it.id })
    }

    @Test
    fun `首份快照里已经完成的任务不上报`() {
        val tracker = TaskCompletionTracker()

        val completed = tracker.collect(listOf(task("a", AgentLifecycle.SUCCEEDED)))

        assertEquals(emptyList<String>(), completed.map { it.id })
    }

    @Test
    fun `已在终态的快照重复到达不会重复上报`() {
        val tracker = TaskCompletionTracker()
        tracker.collect(listOf(task("a", AgentLifecycle.RUNNING)))
        tracker.collect(listOf(task("a", AgentLifecycle.SUCCEEDED)))

        val again = tracker.collect(listOf(task("a", AgentLifecycle.SUCCEEDED)))

        assertEquals(emptyList<String>(), again.map { it.id })
    }

    @Test
    fun `中断同样算完成一并上报`() {
        val tracker = TaskCompletionTracker()
        tracker.collect(listOf(task("a", AgentLifecycle.WAITING_ANSWER)))

        val completed = tracker.collect(listOf(task("a", AgentLifecycle.INTERRUPTED)))

        assertEquals(listOf("a"), completed.map { it.id })
    }

    @Test
    fun `消失后重新出现且已完成的任务不补报`() {
        val tracker = TaskCompletionTracker()
        tracker.collect(listOf(task("a", AgentLifecycle.RUNNING)))
        tracker.collect(emptyList())

        val completed = tracker.collect(listOf(task("a", AgentLifecycle.SUCCEEDED)))

        assertEquals(emptyList<String>(), completed.map { it.id })
    }

    private fun task(id: String, lifecycle: AgentLifecycle) = TaskSnapshot(
        id = id,
        source = AgentSource.CODEX_CLI,
        projectName = "AgentGrid",
        lifecycle = lifecycle,
        startedAt = 0,
        updatedAt = 0,
    )
}
