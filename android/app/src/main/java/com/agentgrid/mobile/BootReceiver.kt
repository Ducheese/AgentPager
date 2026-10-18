package com.agentgrid.mobile

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat
import com.agentgrid.mobile.service.LinkKeepAliveService

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        // 键名与 PairingStore 对齐（agentgrid-pairing 的 payload），改一处必须改两处。
        // 没配对就不起服务：避免开机闪一条毫无意义的常驻通知。
        val stored = context.getSharedPreferences("agentgrid-pairing", Context.MODE_PRIVATE)
        if (!stored.contains("payload")) return

        // 开机只拉后台保活服务，不再拉起界面（Android 10+ 后台起界面本来就会被系统拦截）。
        // BOOT_COMPLETED 属于前台服务启动限制的豁免场景，specialUse 类型不在禁启名单内。
        runCatching {
            ContextCompat.startForegroundService(
                context,
                Intent(context, LinkKeepAliveService::class.java),
            )
        }
    }
}
