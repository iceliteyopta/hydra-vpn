package io.nekohasekai.sfa.compose.screen.settings

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import io.nekohasekai.sfa.R
import io.nekohasekai.sfa.bg.HydraRelays
import io.nekohasekai.sfa.database.ProfileManager
import io.nekohasekai.sfa.database.Settings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File

@Composable
internal fun HydraRelaySettings() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var profileId by remember { mutableLongStateOf(-1L) }
    var choices by remember { mutableStateOf<Map<String, Boolean>>(emptyMap()) }
    var tag by remember { mutableStateOf("") }
    var warning by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val missing = stringResource(R.string.hydra_relay_missing)
    val changed = stringResource(R.string.hydra_conflict)
    TextButton(enabled = !busy, onClick = {
        busy = true
        scope.launch {
            try {
                val (id, items) = withContext(Dispatchers.IO) {
                    val p = ProfileManager.get(Settings.selectedProfile) ?: error(missing)
                    val config = JSONObject(File(p.typed.path).readText())
                    p.id to HydraRelays.tunnels(config).associate { it.getString("tag") to HydraRelays.canBindWifi(config, it) }
                }
                check(items.isNotEmpty()) { missing }
                profileId = id; tag = items.keys.first(); choices = items; warning = null
            } catch (e: Exception) { warning = e.localizedMessage } finally { busy = false }
        }
    }) { Text(stringResource(R.string.hydra_relay_title)) }
    warning?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    if (choices.isEmpty()) return
    key(profileId, tag) {
        val saved = remember { HydraRelays.read(context, profileId, tag) }
        var host by remember { mutableStateOf(saved?.host.orEmpty()) }
        var port by remember { mutableStateOf(saved?.port?.toString().orEmpty()) }
        var wifi by remember { mutableStateOf(saved?.wifiOnly == true && choices[tag] == true) }
        var problem by remember { mutableStateOf<String?>(null) }
        val invalid = stringResource(R.string.hydra_relay_invalid)
        val close = { choices = emptyMap<String, Boolean>() }
        AlertDialog(onDismissRequest = { if (!busy) close() }, title = { Text(stringResource(R.string.hydra_relay_title)) }, text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Choice(stringResource(R.string.hydra_relay_tunnel), tag, choices.keys.toList()) { tag = it }
                Text(stringResource(R.string.hydra_relay_hint))
                OutlinedTextField(host, { host = it }, singleLine = true, label = { Text(stringResource(R.string.hydra_relay_host)) })
                OutlinedTextField(port, { port = it }, singleLine = true, label = { Text(stringResource(R.string.hydra_relay_port)) })
                if (choices[tag] == true) Row {
                    Text(stringResource(R.string.hydra_relay_wifi), Modifier.weight(1f))
                    Switch(checked = wifi, onCheckedChange = { wifi = it })
                }
                Text(stringResource(R.string.hydra_apply_next), style = MaterialTheme.typography.bodySmall)
                problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        }, confirmButton = { TextButton(enabled = !busy, onClick = {
            val server = HydraRelays.host(host)
            val number = port.trim().toIntOrNull()
            if (host.isNotBlank() && (server == null || (port.isNotBlank() && (number == null || number !in 1..65535)))) {
                problem = invalid
            } else {
                busy = true
                scope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            check(Settings.selectedProfile == profileId) { changed }
                            HydraRelays.save(context, profileId, tag, server?.let { HydraRelays.Relay(it, number, wifi) })
                        }
                        close()
                    } catch (e: Exception) { problem = e.localizedMessage } finally { busy = false }
                }
            }
        }) { Text(stringResource(R.string.hydra_save)) } },
            dismissButton = { TextButton(enabled = !busy, onClick = close) { Text(stringResource(android.R.string.cancel)) } })
    }
}
