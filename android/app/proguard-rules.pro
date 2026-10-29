# The engine is Go, called through generated bindings: its classes are reached from native code by
# name, so shrinking must leave them alone.
-keep class go.** { *; }
-keep class io.nekohasekai.libbox.** { *; }
-dontwarn go.**

# Read by name from JSON.
-keepclassmembers class io.github.vexorter42.tunor.** {
    @kotlinx.serialization.Serializable *;
}
