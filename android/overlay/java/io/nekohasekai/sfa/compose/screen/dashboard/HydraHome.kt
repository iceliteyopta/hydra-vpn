package io.nekohasekai.sfa.compose.screen.dashboard

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import io.nekohasekai.sfa.compose.theme.*
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import io.nekohasekai.sfa.R
import io.nekohasekai.sfa.compose.screen.configuration.ProfileImportHandler
import io.nekohasekai.sfa.constant.Status
import kotlinx.coroutines.launch

@Composable
fun HydraHome(state: DashboardUiState, toggle: () -> Unit) {
    val context = LocalContext.current
    val importer = remember(context) { ProfileImportHandler(context) }
    val scope = rememberCoroutineScope()
    var link by remember { mutableStateOf("") }
    var editing by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    fun submit(load: suspend () -> ProfileImportHandler.ImportResult) {
        busy = true
        problem = null
        scope.launch {
            try {
                when (val result = load()) {
                    is ProfileImportHandler.ImportResult.Error -> problem = result.message
                    is ProfileImportHandler.ImportResult.Success -> { editing = false; link = "" }
                }
            } finally { busy = false }
        }
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) submit { importer.importFromUri(uri) }
    }
    val idle = state.serviceStatus == Status.Stopped
    val active = state.serviceStatus == Status.Started
    Column(
        modifier = Modifier.fillMaxWidth().padding(vertical = 12.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        val canToggle = state.selectedProfileId >= 0 && (idle || active) && !busy
        val ring = Brush.linearGradient(listOf(HydraCyan, HydraViolet))
        Box(
            modifier = Modifier.size(164.dp).clip(CircleShape)
                .background(Brush.verticalGradient(listOf(Color(0xff182838), HydraPanel)))
                .border(3.dp, ring, CircleShape)
                .clickable(enabled = canToggle, role = Role.Button, onClick = toggle)
                .padding(12.dp).border(1.dp, HydraBorder, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(10.dp)) {
                val ink = if (active) HydraCyan else HydraMuted
                Canvas(Modifier.size(52.dp)) {
                    val stroke = 3.5.dp.toPx()
                    drawArc(ink, -62f, 304f, false, Offset(stroke, stroke),
                        androidx.compose.ui.geometry.Size(size.width - 2 * stroke, size.height - 2 * stroke),
                        style = Stroke(stroke, cap = StrokeCap.Round))
                    drawLine(ink, Offset(size.width / 2, stroke), Offset(size.width / 2, size.height * .45f), stroke, StrokeCap.Round)
                }
                Text(stringResource(if (active) R.string.hydra_disconnect else R.string.hydra_connect),
                    color = ink, style = MaterialTheme.typography.labelLarge)
            }
        }
        Text(stringResource(when (state.serviceStatus) {
            Status.Stopped -> R.string.hydra_off
            Status.Starting -> R.string.hydra_starting
            Status.Started -> R.string.hydra_on
            Status.Stopping -> R.string.hydra_stopping
        }), color = if (active) HydraCyan else HydraText, style = MaterialTheme.typography.headlineMedium,
            fontWeight = FontWeight.Bold)
        Text(state.selectedProfileName ?: stringResource(R.string.hydra_empty), color = HydraMuted)        if (idle) {
            if (editing) {
                OutlinedTextField(value = link, onValueChange = { link = it }, singleLine = true,
                    modifier = Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface, RoundedCornerShape(4.dp)),
                    label = { Text("hy2://") }, visualTransformation = PasswordVisualTransformation())
                Button(onClick = { submit { importer.importFromQRCode(link.trim()) } },
                    enabled = !busy && (link.trim().startsWith("hy2://") || link.trim().startsWith("hysteria2://"))) {
                    Text(stringResource(R.string.hydra_add))
                }
            } else {
                Button(onClick = { editing = true; problem = null }, enabled = !busy) {
                    Text(stringResource(R.string.hydra_link))
                }
            }
            TextButton(onClick = { picker.launch(arrayOf("*/*")) }, enabled = !busy) {
                Text(stringResource(R.string.hydra_file), color = HydraCyan)
            }
        }
        problem?.let { Text(it, color = Color(0xffffb4ab)) }
    }
}
