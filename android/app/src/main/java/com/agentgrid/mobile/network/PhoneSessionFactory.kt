package com.agentgrid.mobile.network

import android.content.Context
import android.provider.Settings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

object PhoneSessionFactory {
    @Volatile
    private var instance: PhoneSession? = null

    /**
     * 进程级单例：连接不再跟随 Activity/ViewModel 生死。
     * 前台保活服务和界面拿到的是同一条会话——界面关掉/被回收，服务这边还在连。
     */
    fun shared(context: Context): PhoneSession {
        instance?.let { return it }
        return synchronized(this) {
            instance ?: create(context.applicationContext).also { instance = it }
        }
    }

    private fun create(context: Context): PhoneSession =
        DefaultPhoneSession(
            credentialStore = PairingStore(context),
            transport = OkHttpPhoneSessionTransport(),
            deviceID = Settings.Secure.getString(
                context.contentResolver,
                Settings.Secure.ANDROID_ID,
            ) ?: "agentgrid-android",
            scope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
        )
}
