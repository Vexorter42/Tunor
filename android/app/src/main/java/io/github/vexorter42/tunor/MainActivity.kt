package io.github.vexorter42.tunor

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import io.github.vexorter42.tunor.bg.Phase
import io.github.vexorter42.tunor.bg.Vpn
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.ui.App
import io.github.vexorter42.tunor.ui.MainViewModel
import io.github.vexorter42.tunor.ui.TunorTheme

class MainActivity : ComponentActivity() {
    private val vm: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)

        setContent {
            TunorTheme {
                // The system asks the person once whether this app may be a VPN; only then can the service start.
                val consent = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { r ->
                    if (r.resultCode == Activity.RESULT_OK) Vpn.start(this)
                }
                App(vm) {
                    val phase = Vpn.phase.value
                    if (phase == Phase.Started || phase == Phase.Starting) {
                        Vpn.stop(this)
                        return@App
                    }
                    ConfigBuilder.problem(TunorApp.store.state.value)?.let {
                        vm.say(it)
                        return@App
                    }
                    val ask = VpnService.prepare(this)
                    if (ask != null) consent.launch(ask) else Vpn.start(this)
                }
            }
        }
    }
}
