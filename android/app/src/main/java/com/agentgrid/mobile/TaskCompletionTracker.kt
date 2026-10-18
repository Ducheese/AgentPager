package com.agentgrid.mobile

import com.agentgrid.mobile.domain.TaskSnapshot

/**
 * 任务进入完成态（SUCCEEDED / INTERRUPTED）的变更检测。
 *
 * 只有本进程亲眼看到「非终态 → 终态」的那一跳才上报；首份快照里本来就已完成的任务不上报
 * ——服务重启/开机拉起后不会把历史任务翻出来重新提醒一遍。
 */
class TaskCompletionTracker {
    private var known: Map<String, TaskSnapshot> = emptyMap()

    fun collect(tasks: List<TaskSnapshot>): List<TaskSnapshot> {
        val completed = tasks.filter { task ->
            val previous = known[task.id]
            previous != null && !previous.isTerminal && task.isTerminal
        }
        known = tasks.associateBy { it.id }
        return completed
    }
}
