package io.nekohasekai.sfa.compose.screen.configuration

internal object HydraImporter {
    init { System.loadLibrary("hydra_import") }
    @JvmStatic external fun parse(link: String): String
}
