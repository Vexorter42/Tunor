package io.github.vexorter42.tunor.core

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.json.Json
import java.io.File

/**
 * Everything the person has set up, in one file in the app's private storage and one observable value.
 *
 * Written whole and replaced in one step, so that a phone that dies in the middle of a save has the old
 * file or the new one and never half of each.
 */
class Store(context: Context) {
    private val file = File(context.filesDir, "tunor.json")
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; prettyPrint = true }

    private val flow = MutableStateFlow(read())
    val state: StateFlow<State> = flow.asStateFlow()

    private fun read(): State = try {
        if (file.exists()) json.decodeFromString(State.serializer(), file.readText()).migrated() else State()
    } catch (e: Exception) {
        State()          // an unreadable file is the same as none: the person starts again, the app does not crash
    }

    @Synchronized
    fun update(change: (State) -> State) {
        val next = change(flow.value)
        val tmp = File(file.parentFile, file.name + ".tmp")
        tmp.writeText(json.encodeToString(State.serializer(), next))
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
        flow.value = next
    }
}
