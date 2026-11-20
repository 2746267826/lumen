package dev.lumen.app

import android.app.Application
import dev.lumen.app.data.AppStore
import dev.lumen.app.data.SettingsStore
import dev.lumen.app.discovery.LanDiscovery
import dev.lumen.app.net.ApiClient
import dev.lumen.app.net.RelaySocket
import dev.lumen.app.transfer.DirectServer
import dev.lumen.app.transfer.TransferCoordinator
import dev.lumen.app.util.FileStore

class LumenApplication : Application() {
    lateinit var settings: SettingsStore
        private set
    lateinit var store: AppStore
        private set
    lateinit var files: FileStore
        private set
    lateinit var api: ApiClient
        private set
    lateinit var relay: RelaySocket
        private set
    lateinit var discovery: LanDiscovery
        private set
    lateinit var directServer: DirectServer
        private set
    lateinit var coordinator: TransferCoordinator
        private set

    override fun onCreate() {
        super.onCreate()
        settings = SettingsStore(this)
        store = AppStore(this)
        files = FileStore(this)
        api = ApiClient(settings)
        relay = RelaySocket(settings, api.http)
        discovery = LanDiscovery(this, settings)
        directServer = DirectServer(settings, store, files, discovery)
        coordinator = TransferCoordinator(
            api = api,
            settings = settings,
            store = store,
            files = files,
            relay = relay,
            discovery = discovery,
            directServer = directServer
        )
    }
}
