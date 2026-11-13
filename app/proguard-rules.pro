# gomobile-generated bindings reflective entry points — keep them intact.
-keep class mobileapi.** { *; }
-keep interface mobileapi.** { *; }
-dontwarn mobileapi.**

# JSON used by the engine and stats reporting — org/json raises no reflection needs, but be explicit.
-keep class org.json.** { *; }

# Keep app's own service, since the system component is instantiated by package name.
-keep class com.warpconnect.app.MainActivity { <init>(); }
-keep class com.warpconnect.app.WarpVpnService { <init>(); }

# Kotlin coroutines state machines — the engine uses suspension primitives via JNI.
-keepclassmembers class kotlinx.coroutines.** { volatile <fields>; }

# Keep Compose of generated ones not needed; default optimize handles it.

# Firebase Crashlytics — line numbers and signatures needed to symbolicate.
-keepattributes SourceFile,LineNumberTable,Signature,InnerClasses,EnclosingMethod
# Preserve class names for crash client-side grouping.
-keep class com.google.firebase.** { *; }
# Keep native crash entry points.
-keep class com.google.firebase.crashlytics.internal.** { *; }
-dontwarn com.google.firebase.**
# Keep task API signatures for Tasks callbacks reflection.
-keep class com.google.android.gms.tasks.** { *; }
-keep class com.google.android.gms.tasks.Task { *; }
