plugins { kotlin("jvm"); application; jacoco }
kotlin { jvmToolchain(21) }
java { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach { compilerOptions.jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) }
dependencies { implementation("com.google.code.gson:gson:2.10.1"); testImplementation("junit:junit:4.13.2") }
application { mainClass.set("com.zhanh.bluebridge.core.HeadlessPeerKt") }
val headless by sourceSets.creating {
    compileClasspath += sourceSets.main.get().output
    runtimeClasspath += sourceSets.main.get().output
}
configurations[headless.implementationConfigurationName].extendsFrom(configurations.implementation.get())
val headlessJar by tasks.registering(Jar::class) { archiveClassifier.set("headless"); from(headless.output) }
tasks.named<CreateStartScripts>("startScripts") { dependsOn(headlessJar); classpath = files(headlessJar, tasks.jar, configurations.runtimeClasspath) }
distributions { main { contents { from(headlessJar) { into("lib") } } } }
jacoco { toolVersion = "0.8.13" }
tasks.test { systemProperty("goldenPath", rootProject.file("../protocol/golden.json").absolutePath); finalizedBy(tasks.jacocoTestReport) }
tasks.jacocoTestReport { dependsOn(tasks.test); reports { xml.required.set(true); html.required.set(true) }; classDirectories.setFrom(files(classDirectories.files.map { fileTree(it) { exclude("**/HeadlessPeer*", "**/TransferSource*", "**/SessionListener*") } })) }
tasks.jacocoTestCoverageVerification { dependsOn(tasks.jacocoTestReport); classDirectories.setFrom(tasks.jacocoTestReport.get().classDirectories); violationRules { rule { limit { counter = "LINE"; minimum = "0.80".toBigDecimal() } } } }
