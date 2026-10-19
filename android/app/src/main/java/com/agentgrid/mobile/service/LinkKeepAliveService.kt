package com.agentgrid.mobile.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.agentgrid.mobile.MainActivity
import com.agentgrid.mobile.R
import com.agentgrid.mobile.ReplySpeech
import com.agentgrid.mobile.ReplySpeechText
import com.agentgrid.mobile.TaskCompletionTracker
import com.agentgrid.mobile.domain.AgentLifecycle
import com.agentgrid.mobile.domain.TaskSnapshot
import com.agentgrid.mobile.network.PhoneSession
import com.agentgrid.mobile.network.PhoneSessionFactory
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * 前台保活服务：把进程从 cached-app freezer 里捞出来，桥的 WebSocket 才能在后台续命。
 * 选 specialUse 而不是 dataSync —— dataSync 在 Android 15+ 有 6 小时/24 小时上限，到点会被系统掐掉。
 *
 * 顺带当两个"该重连了"的信号源：网络恢复（NetworkCallback）与亮屏（ACTION_SCREEN_ON），
 * 两者都只是调 session.nudge()，真正的重连逻辑在会话里。
 */
class LinkKeepAliveService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var session: PhoneSession
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var screenReceiver: BroadcastReceiver? = null
    private var foregroundStarted = false
    private val completionTracker = TaskCompletionTracker()
    private var lastAlertAt = 0L

    override fun onCreate() {
        super.onCreate()
        session = PhoneSessionFactory.shared(applicationContext)
        createChannels()
        registerNudgeSources()
        // 朗读开始/结束时刷新通知里的「停止朗读」动作（面板收起后用户唯一的停止入口）
        scope.launch {
            ReplySpeech.speakingTaskId.collect {
                if (foregroundStarted) refreshNotification()
            }
        }
        // 完成态提醒：服务全天候收快照，app 开着关着都能报
        scope.launch {
            session.state.collect { state ->
                completionTracker.collect(state.snapshot.tasks)
                    .forEach(::postCompletionNotification)
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP_SPEECH) {
            ReplySpeech.stop()
        }
        startForeground(NOTIFICATION_ID, buildNotification())
        foregroundStarted = true
        scope.launch {
            session.start()
            // 开机拉起但没有配对（或被解除）时没有活可保，自己退场，不占着一条常驻通知。
            if (session.state.value.pairing == null) stopSelf()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        networkCallback?.let { callback ->
            runCatching {
                getSystemService(ConnectivityManager::class.java)?.unregisterNetworkCallback(callback)
            }
        }
        screenReceiver?.let { receiver ->
            runCatching { unregisterReceiver(receiver) }
        }
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun registerNudgeSources() {
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                session.nudge()
            }
        }
        runCatching {
            getSystemService(ConnectivityManager::class.java)
                ?.registerDefaultNetworkCallback(callback)
        }.onSuccess { networkCallback = callback }

        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                session.nudge()
            }
        }
        runCatching {
            registerReceiver(receiver, IntentFilter(Intent.ACTION_SCREEN_ON))
        }.onSuccess { screenReceiver = receiver }
    }

    private fun createChannels() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "连接保活",
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "在后台维持与 AgentPager Bridge 的连接"
                setShowBadge(false)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(
                COMPLETION_CHANNEL_ID,
                "任务完成",
                NotificationManager.IMPORTANCE_DEFAULT,
            ).apply {
                description = "任务进入完成/中断终态时提醒"
            },
        )
    }

    private fun buildNotification(): Notification {
        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_link_alive)
            .setContentTitle("AgentPager")
            .setContentText("保持与 Bridge 的连接")
            .setContentIntent(launchAppIntent())
            .setOngoing(true)
            .setShowWhen(false)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
        if (ReplySpeech.speakingTaskId.value != null) {
            val stopSpeech = PendingIntent.getService(
                this,
                1,
                Intent(this, LinkKeepAliveService::class.java).setAction(ACTION_STOP_SPEECH),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            builder.addAction(0, "停止朗读", stopSpeech)
        }
        return builder.build()
    }

    private fun refreshNotification() {
        runCatching {
            getSystemService(NotificationManager::class.java)
                .notify(NOTIFICATION_ID, buildNotification())
        }
    }

    private fun postCompletionNotification(task: TaskSnapshot) {
        val label = if (task.lifecycle == AgentLifecycle.INTERRUPTED) "任务中断" else "任务完成"
        val preview = task.responseBody
            ?.let(ReplySpeechText::speakable)
            ?.takeIf { it.isNotBlank() }
            ?.let { if (it.length > 140) it.take(140) + "…" else it }
        val text = preview?.let { "$label：$it" } ?: label
        val notificationId = completionNotificationId(task.id)
        // 批量完成只让第一条响：8 秒窗口内的后续进通知栏但静音。
        // 不能指望 setOnlyAlertOnce —— 它只压制**同一个 id** 的更新，不同任务照响。
        val now = System.currentTimeMillis()
        val aloud = now - lastAlertAt >= COMPLETION_ALERT_WINDOW_MS
        if (aloud) lastAlertAt = now
        val notification = NotificationCompat.Builder(this, COMPLETION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_task_done)
            .setContentTitle(task.title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(launchTaskIntent(task.id, notificationId))
            .setAutoCancel(true)
            .setShowWhen(true)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setGroup(COMPLETION_GROUP)
            .setSilent(!aloud)
            .build()
        runCatching {
            val manager = getSystemService(NotificationManager::class.java)
            manager.notify(notificationId, notification)
            // 有子通知时才需要 summary；静默，不重复响铃。
            manager.notify(
                COMPLETION_SUMMARY_ID,
                NotificationCompat.Builder(this, COMPLETION_CHANNEL_ID)
                    .setSmallIcon(R.drawable.ic_task_done)
                    .setContentTitle("AgentPager 任务完成")
                    .setContentText("点开可回到对应任务")
                    .setContentIntent(launchAppIntent())
                    .setGroup(COMPLETION_GROUP)
                    .setGroupSummary(true)
                    .setSilent(true)
                    .setOnlyAlertOnce(true)
                    .setAutoCancel(true)
                    .build(),
            )
        }
    }

    private fun launchTaskIntent(taskID: String, notificationId: Int): PendingIntent =
        PendingIntent.getActivity(
            this,
            notificationId,
            Intent(this, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                .putExtra(MainActivity.EXTRA_TASK_ID, taskID),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

    private fun launchAppIntent(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    companion object {
        private const val CHANNEL_ID = "agentpager-link"
        private const val COMPLETION_CHANNEL_ID = "agentpager-task-completion"
        private const val NOTIFICATION_ID = 0x4A47
        private const val COMPLETION_SUMMARY_ID = 0x4A48
        private const val COMPLETION_GROUP = "agentpager-completion"
        private const val COMPLETION_ALERT_WINDOW_MS = 8_000L
        const val ACTION_STOP_SPEECH = "com.agentgrid.mobile.action.STOP_SPEECH"

        /** 完成通知的固定 id：同一任务重跑完只替换旧提醒；MainActivity 取消时也用它。 */
        fun completionNotificationId(taskID: String): Int = taskID.hashCode().let {
            if (it == NOTIFICATION_ID || it == 0) it + 1 else it
        }
    }
}
