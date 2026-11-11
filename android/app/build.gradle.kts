import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("io.github.takahirom.roborazzi")
}

android {
    namespace = "io.github.theuslpszbr15.dynamicisland"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.theuslpszbr15.dynamicisland"
        // the combined fingerprint / screen-lock prompt needs Android 11
        minSdk = 30
        targetSdk = 35
        versionCode = 2
        versionName = "1.1.0"
    }

    // Release key: android/keystore.properties (storeFile, storePassword, keyAlias, keyPassword; never committed)
    // or the same values in DI_STORE_FILE, DI_STORE_PASSWORD, DI_KEY_ALIAS, DI_KEY_PASSWORD (CI secrets).
    // Without one the release falls back to this machine's debug key, which can't update an APK signed
    // anywhere else, so published APKs must always come from the release key.
    val releaseKey = Properties().apply {
        val file = rootProject.file("keystore.properties")
        if (file.exists()) file.inputStream().use { load(it) }
        System.getenv("DI_STORE_FILE")?.let { setProperty("storeFile", it) }
        System.getenv("DI_STORE_PASSWORD")?.let { setProperty("storePassword", it) }
        System.getenv("DI_KEY_ALIAS")?.let { setProperty("keyAlias", it) }
        System.getenv("DI_KEY_PASSWORD")?.let { setProperty("keyPassword", it) }
    }
    val hasReleaseKey = listOf("storeFile", "storePassword", "keyAlias", "keyPassword").all { !releaseKey.getProperty(it).isNullOrEmpty() }

    signingConfigs {
        if (hasReleaseKey) create("release") {
            storeFile = rootProject.file(releaseKey.getProperty("storeFile"))
            storePassword = releaseKey.getProperty("storePassword")
            keyAlias = releaseKey.getProperty("keyAlias")
            keyPassword = releaseKey.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (hasReleaseKey) signingConfigs.getByName("release") else {
                logger.warn("Dynamic Island: no release key (keystore.properties / DI_*), signing the release with the debug key")
                signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
    }
    testOptions {
        unitTests.isReturnDefaultValues = true
        unitTests.isIncludeAndroidResources = true
        unitTests.all { test ->
            // interop test against a running Dynamic Island: PHONE_LINK=<pairing link>
            System.getenv("PHONE_LINK")?.let { test.environment("PHONE_LINK", it) }
        }
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("androidx.fragment:fragment-ktx:1.8.5")
    implementation("androidx.biometric:biometric:1.1.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    // WebSocket to the relay (away from the PC's network)
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    testImplementation("com.squareup.okhttp3:mockwebserver:4.12.0")
    // screenshots of the Compose screens on the JVM: ./gradlew recordRoborazziRelease
    testImplementation("org.robolectric:robolectric:4.14.1")
    testImplementation("io.github.takahirom.roborazzi:roborazzi:1.39.0")
    testImplementation("io.github.takahirom.roborazzi:roborazzi-compose:1.39.0")
    testImplementation("androidx.compose.ui:ui-test-junit4")
    testImplementation("androidx.test.ext:junit:1.2.1")
}
