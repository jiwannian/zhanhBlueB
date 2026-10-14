plugins { id("com.android.application"); kotlin("android"); id("org.jetbrains.kotlin.plugin.compose"); jacoco }
android {
    namespace = "com.zhanh.bluebridge"
    compileSdk = 36
    defaultConfig { applicationId = "com.zhanh.bluebridge"; minSdk = 29; targetSdk = 36; versionCode = 2; versionName = "1.0.1"; testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true }
    signingConfigs {
        create("release") {
            val path = System.getenv("BLUEBRIDGE_KEYSTORE")
            if (path != null) { storeFile = file(path); storePassword = System.getenv("BLUEBRIDGE_STORE_PASSWORD"); keyAlias = "bluebridge"; keyPassword = System.getenv("BLUEBRIDGE_KEY_PASSWORD") }
        }
    }
    buildTypes { release { isMinifyEnabled = false; signingConfig = signingConfigs.getByName("release") } }
}
jacoco { toolVersion = "0.8.13" }
tasks.withType<Test>().configureEach {
    extensions.configure<JacocoTaskExtension> { isIncludeNoLocationClasses = true; excludes = listOf("jdk.internal.*") }
}
val receivedUiClasses = fileTree(layout.buildDirectory.dir("tmp/kotlin-classes/debug")) { include("**/BridgeStateKt.class") }
val receivedUiReport = tasks.register<JacocoReport>("receivedUiCoverageReport") {
    dependsOn("testDebugUnitTest")
    executionData(layout.buildDirectory.file("jacoco/testDebugUnitTest.exec"))
    classDirectories.setFrom(receivedUiClasses)
    sourceDirectories.setFrom(files("src/main/java"))
    reports { xml.required.set(true); html.required.set(true) }
}
tasks.register<JacocoCoverageVerification>("receivedUiCoverageVerification") {
    dependsOn(receivedUiReport)
    executionData(receivedUiReport.get().executionData)
    classDirectories.setFrom(receivedUiClasses)
    violationRules { rule { limit { counter = "LINE"; minimum = "0.80".toBigDecimal() } } }
}
dependencies {
    implementation(project(":core"))
    implementation(platform("androidx.compose:compose-bom:2025.02.00"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test:rules:1.6.1")
    androidTestImplementation(platform("androidx.compose:compose-bom:2025.02.00"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
