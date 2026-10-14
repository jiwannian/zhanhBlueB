pluginManagement {
    repositories { google(); mavenCentral(); gradlePluginPortal() }
    resolutionStrategy.eachPlugin {
        if (requested.id.id.startsWith("org.jetbrains.kotlin") && requested.id.id != "org.jetbrains.kotlin.plugin.compose") useModule("org.jetbrains.kotlin:kotlin-gradle-plugin:${requested.version}")
    }
}
dependencyResolutionManagement { repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS); repositories { google(); mavenCentral() } }
rootProject.name = "BlueBridgeAndroid"
include(":core", ":app")
