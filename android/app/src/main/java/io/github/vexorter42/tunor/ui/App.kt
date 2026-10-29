package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier

/** Everything on screen: the terms for a new install, and after them the four pages. */
@Composable
fun App(vm: MainViewModel, onToggle: () -> Unit) {
    val state by vm.state.collectAsState()
    if (!state.settings.termsAccepted) {
        TermsScreen(onAccept = vm::acceptTerms)
        return
    }

    var tab by rememberSaveable { mutableIntStateOf(0) }
    val snack = remember { SnackbarHostState() }
    val message by vm.message.collectAsState()
    LaunchedEffect(message) {
        message?.let {
            snack.showSnackbar(it)
            vm.clearMessage()
        }
    }

    Scaffold(
        modifier = Modifier.fillMaxSize(),
        containerColor = Palette.Bg,
        snackbarHost = { SnackbarHost(snack) },
        bottomBar = {
            NavigationBar(containerColor = Palette.Panel) {
                val colors = NavigationBarItemDefaults.colors(
                    selectedIconColor = Palette.OnAccent, selectedTextColor = Palette.Accent, indicatorColor = Palette.Accent,
                    unselectedIconColor = Palette.Dim, unselectedTextColor = Palette.Dim,
                )
                NavigationBarItem(tab == 0, { tab = 0 }, { Icon(Icons.Default.Home, null) }, label = { Text("Главная") }, colors = colors)
                NavigationBarItem(tab == 1, { tab = 1 }, { Icon(Icons.Default.Lock, null) }, label = { Text("Туннели") }, colors = colors)
                NavigationBarItem(tab == 2, { tab = 2 }, { Icon(Icons.AutoMirrored.Filled.List, null) }, label = { Text("Правила") }, colors = colors)
                NavigationBarItem(tab == 3, { tab = 3 }, { Icon(Icons.Default.Settings, null) }, label = { Text("Ещё") }, colors = colors)
            }
        },
    ) { padding ->
        when (tab) {
            0 -> HomeScreen(vm, padding, onToggle)
            1 -> TunnelsScreen(vm, padding)
            2 -> RulesScreen(vm, padding)
            else -> MoreScreen(vm, padding)
        }
    }
}
