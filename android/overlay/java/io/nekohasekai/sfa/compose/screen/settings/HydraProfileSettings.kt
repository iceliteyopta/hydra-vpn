package io.nekohasekai.sfa.compose.screen.settings

import android.content.Intent
import android.provider.Settings as AndroidSettings
import android.util.AtomicFile
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.sfa.R
import io.nekohasekai.sfa.database.ProfileManager
import io.nekohasekai.sfa.database.Settings
import io.nekohasekai.sfa.database.TypedProfile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

private data class EditableProfile(val id: Long, val file: File, val content: String)

private fun JSONObject.section(key: String): JSONObject =
    optJSONObject(key) ?: JSONObject().also { put(key, it) }

private fun tags(array: JSONArray?): List<String> =
    (0 until (array?.length() ?: 0)).mapNotNull { index ->
        array?.optJSONObject(index)?.optString("tag")?.takeIf { it.isNotBlank() }
    }

@Composable
private fun Choice(label: String, value: String, options: List<String>, select: (String) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Column {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Box {
            TextButton(onClick = { expanded = true }) { Text(value.ifBlank { "—" }) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                options.distinct().forEach { item ->
                    DropdownMenuItem(text = { Text(item.ifBlank { "—" }) },
                        onClick = { select(item); expanded = false })
                }
            }
        }
    }
}

@Composable
fun HydraProfileSettings() {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var edit by remember { mutableStateOf<EditableProfile?>(null) }
    var problem by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val noProfile = stringResource(R.string.hydra_empty)
    val remote = stringResource(R.string.hydra_remote)
    Card(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
        Column(Modifier.padding(16.dp)) {
            Text(stringResource(R.string.hydra_profile_settings), style = MaterialTheme.typography.titleMedium)
            TextButton(enabled = !busy, onClick = {
                busy = true
                scope.launch {
                    try {
                        edit = withContext(Dispatchers.IO) {
                            val p = ProfileManager.get(Settings.selectedProfile) ?: error(noProfile)
                            require(p.typed.type == TypedProfile.Type.Local) { remote }
                            val file = File(p.typed.path)
                            val source = file.readText()
                            JSONObject(source)
                            EditableProfile(p.id, file, source)
                        }
                    } catch (e: Exception) { problem = e.localizedMessage } finally { busy = false }
                }
            }) { Text(stringResource(R.string.hydra_edit_profile)) }
            TextButton(onClick = {
                try { context.startActivity(Intent(AndroidSettings.ACTION_VPN_SETTINGS)) }
                catch (e: Exception) { problem = e.localizedMessage }
            }) { Text(stringResource(R.string.hydra_lockdown)) }
            problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        }
    }
    edit?.let { profile -> key(profile.id) { ProfileDialog(profile) { edit = null } } }
}

@Composable
private fun ProfileDialog(profile: EditableProfile, close: () -> Unit) {
    val source = remember { JSONObject(profile.content) }
    val route = source.optJSONObject("route")
    val dns = source.optJSONObject("dns")
    val inboundArray = source.optJSONArray("inbounds") ?: JSONArray()
    val tun = (0 until inboundArray.length()).mapNotNull { inboundArray.optJSONObject(it) }
        .firstOrNull { it.optString("type") == "tun" }
    val destinations = tags(source.optJSONArray("outbounds")) + tags(source.optJSONArray("endpoints"))
    var final by remember { mutableStateOf(route?.optString("final").orEmpty()) }
    var resolver by remember { mutableStateOf(dns?.optString("final").orEmpty()) }
    var level by remember { mutableStateOf(source.optJSONObject("log")?.optString("level","info") ?: "info") }
    var strict by remember { mutableStateOf(tun?.optBoolean("strict_route") ?: false) }
    var rules by remember { mutableStateOf((route?.optJSONArray("rules") ?: JSONArray()).toString(2)) }
    var advanced by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val conflict = stringResource(R.string.hydra_conflict)
    val scope = rememberCoroutineScope()
    AlertDialog(onDismissRequest = { if (!busy) close() },
        title = { Text(stringResource(R.string.hydra_profile_settings)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Choice(stringResource(R.string.hydra_default_route), final, listOf("") + destinations) { final = it }
                Choice(stringResource(R.string.hydra_default_dns), resolver, listOf("") + tags(dns?.optJSONArray("servers"))) { resolver = it }
                Choice(stringResource(R.string.hydra_log_level), level, listOf("trace","debug","info","warn","error","fatal","panic")) { level = it }
                if (tun != null) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(stringResource(R.string.hydra_strict), Modifier.weight(1f))
                        Switch(checked = strict, onCheckedChange = { strict = it })
                    }
                }
                TextButton(onClick = { advanced = !advanced }) { Text(stringResource(R.string.hydra_route_rules)) }
                if (advanced) OutlinedTextField(value = rules, onValueChange = { rules = it },
                    label = { Text(stringResource(R.string.hydra_route_rules)) }, minLines = 3, maxLines = 8)
                Text(stringResource(R.string.hydra_apply_next), style = MaterialTheme.typography.bodySmall)
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = { TextButton(enabled = !busy, onClick = {
            busy = true
            scope.launch {
                try {
                    withContext(Dispatchers.IO) {
                        val updated = JSONObject(profile.content)
                        val r = updated.section("route")
                        if (final.isBlank()) r.remove("final") else r.put("final", final)
                        r.put("rules", JSONArray(rules))
                        if (resolver.isNotBlank()) updated.section("dns").put("final",resolver)
                        else updated.optJSONObject("dns")?.remove("final")
                        updated.section("log").put("level",level)
                        val inbounds = updated.optJSONArray("inbounds")
                        for (i in 0 until (inbounds?.length() ?: 0)) {
                            val item = inbounds?.optJSONObject(i)
                            if (item?.optString("type") == "tun") { item.put("strict_route",strict); break }
                        }
                        val text = updated.toString(2)
                        Libbox.checkConfig(text)
                        check(Settings.selectedProfile == profile.id && profile.file.readText() == profile.content) { conflict }
                        val atomic = AtomicFile(profile.file)
                        val stream = atomic.startWrite()
                        try { stream.write(text.toByteArray()); atomic.finishWrite(stream) }
                        catch (e: Exception) { atomic.failWrite(stream); throw e }
                    }
                    close()
                } catch (e: Exception) { error = e.localizedMessage } finally { busy = false }
            }
        }) { Text(stringResource(R.string.hydra_save)) } },
        dismissButton = { TextButton(enabled = !busy, onClick = close) { Text(stringResource(android.R.string.cancel)) } },
    )
}
