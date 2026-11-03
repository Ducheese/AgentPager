package com.agentgrid.mobile.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
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
            networkKind = { networkKindOf(context) },
        )

    /**
     * 默认网络是不是本地链路。Wi-Fi / 以太网直接判 Local；VPN 本身不携带底层传输，
     * 再看系统里是否还有 Wi-Fi/以太网——只有蜂窝时不保留局域网候选，避免冷启动先拨 LAN。
     */
    @Suppress("DEPRECATION") // allNetworks 没有同步替代；这里只做一次轻量快照，不注册回调。
    private fun networkKindOf(context: Context): NetworkKind {
        val manager = context.getSystemService(ConnectivityManager::class.java)
            ?: return NetworkKind.Unknown
        val network = manager.activeNetwork ?: return NetworkKind.Unknown
        val capabilities = manager.getNetworkCapabilities(network) ?: return NetworkKind.Unknown
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
        ) {
            return NetworkKind.Local
        }
        if (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
            return NetworkKind.Remote
        }
        // VPN 做默认网络时，activeNetwork 只告诉我们是 VPN；看底层是否还有本地链路。
        val hasLocalTransport = manager.allNetworks.any { candidate ->
            if (candidate == network) return@any false
            val local = manager.getNetworkCapabilities(candidate) ?: return@any false
            local.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                local.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
        }
        return if (hasLocalTransport) NetworkKind.Local else NetworkKind.Remote
    }
}
