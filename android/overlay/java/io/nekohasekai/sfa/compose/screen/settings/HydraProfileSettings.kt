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
            HydraMobileOptions()
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
    var outbounds by remember { mutableStateOf((source.optJSONArray("outbounds") ?: JSONArray()).toString()) }
    val outArray = JSONArray(outbounds)
    val tunnels = (0 until outArray.length()).map { outArray.getJSONObject(it) }
        .filter { it.optString("type") !in setOf("direct", "block", "dns", "selector", "urltest") }
        .map { it.optString("tag") }.filter { it.isNotBlank() } + tags(source.optJSONArray("endpoints"))
    val existingTags = tags(outArray) + tags(source.optJSONArray("endpoints"))
    val destinations = (existingTags + "direct").distinct()
    var newTunnel by remember { mutableStateOf(false) }
    var link by remember { mutableStateOf("") }
    val remoteSets = route?.optJSONArray("rule_set")
    val hasRemoteSets = (0 until (remoteSets?.length() ?: 0)).any { remoteSets?.optJSONObject(it)?.optString("type") == "remote" }
    var setVia by remember { mutableStateOf("") }
    var final by remember { mutableStateOf(route?.optString("final").orEmpty()) }
    var dnsServers by remember { mutableStateOf((dns?.optJSONArray("servers") ?: JSONArray()).toString(2)) }
    var editDns by remember { mutableStateOf(false) }
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
                TextButton(onClick = { newTunnel = !newTunnel }) { Text(stringResource(R.string.hydra_add_tunnel)) }
                if (newTunnel) {
                    OutlinedTextField(value = link, onValueChange = { link = it }, singleLine = true,
                        visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(), label = { Text("hy2://") })
                    TextButton(enabled = link.isNotBlank(), onClick = {
                        try {
                            val config = io.nekohasekai.sfa.compose.screen.configuration.HydraImporter.parse(link.trim())
                            val tunnel = JSONObject(config).getJSONArray("outbounds").getJSONObject(0)
                            val base = android.net.Uri.parse(link.trim()).fragment?.takeIf { it.isNotBlank() } ?: "Hysteria2"
                            var name = base
                            var suffix = 2
                            while (name in destinations || name == "block") name = "$base " + suffix++
                            tunnel.put("tag", name)
                            outbounds = JSONArray(outbounds).put(tunnel).toString()
                            newTunnel = false
                            link = ""
                            error = null
                        } catch (e: Exception) { error = e.localizedMessage }
                    }) { Text(stringResource(R.string.hydra_add)) }
                }
                Choice(stringResource(R.string.hydra_default_route), final, listOf("") + destinations) { final = it }
                Choice(stringResource(R.string.hydra_default_dns), resolver, listOf("") + runCatching { tags(JSONArray(dnsServers)) }.getOrDefault(emptyList())) { resolver = it }
                TextButton(onClick = { editDns = !editDns }) { Text(stringResource(R.string.hydra_dns_servers)) }
                if (editDns) OutlinedTextField(value = dnsServers, onValueChange = { dnsServers = it },
                    label = { Text("DNS · JSON") }, minLines = 3, maxLines = 8)
                if (hasRemoteSets) Choice(stringResource(R.string.hydra_rules_via), setVia, listOf("") + destinations) { setVia = it }
                Choice(stringResource(R.string.hydra_log_level), level, listOf("trace","debug","info","warn","error","fatal","panic")) { level = it }
                if (tun != null) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(stringResource(R.string.hydra_strict), Modifier.weight(1f))
                        Switch(checked = strict, onCheckedChange = { strict = it })
                    }
                }
                RuleComposer(destinations, tunnels, rules) { rules = it }
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
                        val entries = JSONArray(rules)
                        r.put("rules", entries)
                        val outs = JSONArray(outbounds)
                        if ("direct" !in existingTags && (final == "direct" || setVia == "direct" ||
                            (0 until entries.length()).any { entries.optJSONObject(it)?.optString("outbound") == "direct" })) {
                            outs.put(JSONObject().put("type", "direct").put("tag", "direct"))
                        }
                        updated.put("outbounds", outs)
                        if (setVia.isNotBlank()) {
                            val sets = r.optJSONArray("rule_set")
                            for (i in 0 until (sets?.length() ?: 0)) {
                                val set = sets?.optJSONObject(i)
                                if (set?.optString("type") == "remote") set.put("download_detour", setVia)
                            }
                        }
                        if (dnsServers != (dns?.optJSONArray("servers") ?: JSONArray()).toString(2))
                            updated.section("dns").put("servers", JSONArray(dnsServers))
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

@Composable
private fun RuleComposer(destinations: List<String>, tunnels: List<String>, rules: String, change: (String) -> Unit) {
    var show by remember { mutableStateOf(false) }
    var protect by remember { mutableStateOf(false) }
    var kind by remember { mutableStateOf("domain_suffix") }
    var value by remember { mutableStateOf("") }
    var dest by remember { mutableStateOf(destinations.firstOrNull().orEmpty()) }
    val context = LocalContext.current
    var apps by remember { mutableStateOf(emptyList<Pair<String,String>>()) }
    var problem by remember { mutableStateOf<String?>(null) }
    TextButton(onClick = { protect = false; show = !show }) { Text(stringResource(R.string.hydra_add_rule)) }

    TextButton(enabled = tunnels.isNotEmpty(), onClick = {
        protect = true; show = true; kind = "package_name"; value = ""; dest = tunnels.first()
    }) { Text(stringResource(R.string.hydra_protect)) }
    if (!show) return
    Choice(stringResource(R.string.hydra_match), kind, if (protect) listOf("package_name","domain_suffix","domain","ip_cidr") else listOf("domain_suffix","domain","domain_keyword","ip_cidr","port","package_name")) { kind = it; value = "" }
    if (kind == "package_name") {
        LaunchedEffect(Unit) {
            apps = withContext(Dispatchers.IO) {
                context.packageManager.getInstalledApplications(0).map { app ->
                    app.loadLabel(context.packageManager).toString() to app.packageName
                }.sortedBy { it.first.lowercase() }
            }
        }
    }
    OutlinedTextField(value = value, onValueChange = { value = it }, singleLine = true,
        label = { Text(if (kind == "package_name") stringResource(R.string.hydra_find_app) else kind) })
    if (kind == "package_name" && apps.none { it.second == value }) {
        apps.asSequence().filter { it.first.contains(value,true) || it.second.contains(value,true) }.take(5).forEach { app ->
            TextButton(onClick = { value = app.second }) { Text(app.first + " · " + app.second) }
        }
    }
    Choice(stringResource(R.string.hydra_default_route), dest, if (protect) tunnels else destinations + "block") { dest = it }
    TextButton(enabled = value.isNotBlank() && dest.isNotBlank(), onClick = {
        try {
            val values = JSONArray()
            value.split(',', ';').map { it.trim() }.filter { it.isNotEmpty() }.forEach {
                if (kind == "port") {
                    val port = it.toInt()
                    require(port in 1..65535)
                    values.put(port)
                } else values.put(it)
            }
            require(values.length() > 0)
            val added = JSONObject().put(kind, values)
            if (dest == "block") added.put("action","reject") else added.put("outbound",dest)
            val old = JSONArray(rules)
            val result = JSONArray()
            var inserted = false
            for (i in 0 until old.length()) {
                val item = old.getJSONObject(i)
                if (!inserted && item.optString("action") !in listOf("sniff","hijack-dns","resolve","route-options")) {
                    result.put(added); inserted = true
                }
                result.put(item)
            }
            if (!inserted) result.put(added)
            change(result.toString(2)); show = false; value = ""; problem = null
        } catch (e: Exception) { problem = e.localizedMessage ?: "Invalid rule" }
    }) { Text(stringResource(R.string.hydra_add_rule)) }
    problem?.let { Text(it, color = MaterialTheme.colorScheme.error) }
}