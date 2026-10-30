# The engine is Go, called through generated bindings: its classes are reached from native code by
# name, so shrinking must leave them alone.
-keep class go.** { *; }
-keep class io.nekohasekai.libbox.** { *; }
-dontwarn go.**

# A report's stack has the file and the line of each step, so that a fault can be found in the code. The class names are
# shortened in a release; the mapping.txt of the same build (app/build/outputs/mapping/release) turns them back.
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile

# Read by name from JSON.
-keepclassmembers class io.github.vexorter42.tunor.** {
    @kotlinx.serialization.Serializable *;
}
