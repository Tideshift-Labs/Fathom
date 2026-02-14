@file:Suppress("EXPERIMENTAL_API_USAGE","EXPERIMENTAL_UNSIGNED_LITERALS","PackageDirectoryMismatch","UnusedImport","unused","LocalVariableName","CanBeVal","PropertyName","EnumEntryName","ClassName","ObjectPropertyName","UnnecessaryVariable","SpellCheckingInspection")
package com.jetbrains.rd.ide.model

import com.jetbrains.rd.framework.*
import com.jetbrains.rd.framework.base.*
import com.jetbrains.rd.framework.impl.*

import com.jetbrains.rd.util.lifetime.*
import com.jetbrains.rd.util.reactive.*
import com.jetbrains.rd.util.string.*
import com.jetbrains.rd.util.*
import kotlin.time.Duration
import kotlin.reflect.KClass
import kotlin.jvm.JvmStatic



/**
 * #### Generated from [FathomModel.kt:8]
 */
class FathomModel private constructor(
    private val _port: RdOptionalProperty<Int>,
    private val _serverStatus: RdSignal<ServerStatus>,
    private val _companionPluginStatus: RdSignal<CompanionPluginInfo>,
    private val _installCompanionPlugin: RdSignal<String>,
    private val _buildCompanionPlugin: RdSignal<Unit>,
    private val _mcpConfigStatus: RdSignal<String>
) : RdExtBase() {
    //companion
    
    companion object : ISerializersOwner {
        
        override fun registerSerializersCore(serializers: ISerializers)  {
            val classLoader = javaClass.classLoader
            serializers.register(LazyCompanionMarshaller(RdId(-1286347501835911992), classLoader, "com.jetbrains.rd.ide.model.ServerStatus"))
            serializers.register(LazyCompanionMarshaller(RdId(-8935656586553858082), classLoader, "com.jetbrains.rd.ide.model.CompanionPluginStatus"))
            serializers.register(LazyCompanionMarshaller(RdId(-2063202156580403302), classLoader, "com.jetbrains.rd.ide.model.CompanionPluginInfo"))
        }
        
        
        
        
        
        const val serializationHash = 1387835151668611458L
        
    }
    override val serializersOwner: ISerializersOwner get() = FathomModel
    override val serializationHash: Long get() = FathomModel.serializationHash
    
    //fields
    val port: IOptProperty<Int> get() = _port
    val serverStatus: ISignal<ServerStatus> get() = _serverStatus
    val companionPluginStatus: ISource<CompanionPluginInfo> get() = _companionPluginStatus
    val installCompanionPlugin: ISignal<String> get() = _installCompanionPlugin
    val buildCompanionPlugin: ISignal<Unit> get() = _buildCompanionPlugin
    val mcpConfigStatus: ISignal<String> get() = _mcpConfigStatus
    //methods
    //initializer
    init {
        _port.optimizeNested = true
    }
    
    init {
        bindableChildren.add("port" to _port)
        bindableChildren.add("serverStatus" to _serverStatus)
        bindableChildren.add("companionPluginStatus" to _companionPluginStatus)
        bindableChildren.add("installCompanionPlugin" to _installCompanionPlugin)
        bindableChildren.add("buildCompanionPlugin" to _buildCompanionPlugin)
        bindableChildren.add("mcpConfigStatus" to _mcpConfigStatus)
    }
    
    //secondary constructor
    internal constructor(
    ) : this(
        RdOptionalProperty<Int>(FrameworkMarshallers.Int),
        RdSignal<ServerStatus>(ServerStatus),
        RdSignal<CompanionPluginInfo>(CompanionPluginInfo),
        RdSignal<String>(FrameworkMarshallers.String),
        RdSignal<Unit>(FrameworkMarshallers.Void),
        RdSignal<String>(FrameworkMarshallers.String)
    )
    
    //equals trait
    //hash code trait
    //pretty print
    override fun print(printer: PrettyPrinter)  {
        printer.println("FathomModel (")
        printer.indent {
            print("port = "); _port.print(printer); println()
            print("serverStatus = "); _serverStatus.print(printer); println()
            print("companionPluginStatus = "); _companionPluginStatus.print(printer); println()
            print("installCompanionPlugin = "); _installCompanionPlugin.print(printer); println()
            print("buildCompanionPlugin = "); _buildCompanionPlugin.print(printer); println()
            print("mcpConfigStatus = "); _mcpConfigStatus.print(printer); println()
        }
        printer.print(")")
    }
    //deepClone
    override fun deepClone(): FathomModel   {
        return FathomModel(
            _port.deepClonePolymorphic(),
            _serverStatus.deepClonePolymorphic(),
            _companionPluginStatus.deepClonePolymorphic(),
            _installCompanionPlugin.deepClonePolymorphic(),
            _buildCompanionPlugin.deepClonePolymorphic(),
            _mcpConfigStatus.deepClonePolymorphic()
        )
    }
    //contexts
    //threading
    override val extThreading: ExtThreadingKind get() = ExtThreadingKind.Default
}
val Solution.fathomModel get() = getOrCreateExtension("fathomModel", ::FathomModel)



/**
 * #### Generated from [FathomModel.kt:23]
 */
data class CompanionPluginInfo (
    val status: CompanionPluginStatus,
    val installedVersion: String,
    val bundledVersion: String,
    val installLocation: String,
    val message: String
) : IPrintable {
    //companion
    
    companion object : IMarshaller<CompanionPluginInfo> {
        override val _type: KClass<CompanionPluginInfo> = CompanionPluginInfo::class
        override val id: RdId get() = RdId(-2063202156580403302)
        
        @Suppress("UNCHECKED_CAST")
        override fun read(ctx: SerializationCtx, buffer: AbstractBuffer): CompanionPluginInfo  {
            val status = buffer.readEnum<CompanionPluginStatus>()
            val installedVersion = buffer.readString()
            val bundledVersion = buffer.readString()
            val installLocation = buffer.readString()
            val message = buffer.readString()
            return CompanionPluginInfo(status, installedVersion, bundledVersion, installLocation, message)
        }
        
        override fun write(ctx: SerializationCtx, buffer: AbstractBuffer, value: CompanionPluginInfo)  {
            buffer.writeEnum(value.status)
            buffer.writeString(value.installedVersion)
            buffer.writeString(value.bundledVersion)
            buffer.writeString(value.installLocation)
            buffer.writeString(value.message)
        }
        
        
    }
    //fields
    //methods
    //initializer
    //secondary constructor
    //equals trait
    override fun equals(other: Any?): Boolean  {
        if (this === other) return true
        if (other == null || other::class != this::class) return false
        
        other as CompanionPluginInfo
        
        if (status != other.status) return false
        if (installedVersion != other.installedVersion) return false
        if (bundledVersion != other.bundledVersion) return false
        if (installLocation != other.installLocation) return false
        if (message != other.message) return false
        
        return true
    }
    //hash code trait
    override fun hashCode(): Int  {
        var __r = 0
        __r = __r*31 + status.hashCode()
        __r = __r*31 + installedVersion.hashCode()
        __r = __r*31 + bundledVersion.hashCode()
        __r = __r*31 + installLocation.hashCode()
        __r = __r*31 + message.hashCode()
        return __r
    }
    //pretty print
    override fun print(printer: PrettyPrinter)  {
        printer.println("CompanionPluginInfo (")
        printer.indent {
            print("status = "); status.print(printer); println()
            print("installedVersion = "); installedVersion.print(printer); println()
            print("bundledVersion = "); bundledVersion.print(printer); println()
            print("installLocation = "); installLocation.print(printer); println()
            print("message = "); message.print(printer); println()
        }
        printer.print(")")
    }
    //deepClone
    //contexts
    //threading
}


/**
 * #### Generated from [FathomModel.kt:16]
 */
enum class CompanionPluginStatus {
    NotInstalled, 
    Outdated, 
    Installed, 
    UpToDate;
    
    companion object : IMarshaller<CompanionPluginStatus> {
        val marshaller = FrameworkMarshallers.enum<CompanionPluginStatus>()
        
        
        override val _type: KClass<CompanionPluginStatus> = CompanionPluginStatus::class
        override val id: RdId get() = RdId(-8935656586553858082)
        
        override fun read(ctx: SerializationCtx, buffer: AbstractBuffer): CompanionPluginStatus {
            return marshaller.read(ctx, buffer)
        }
        
        override fun write(ctx: SerializationCtx, buffer: AbstractBuffer, value: CompanionPluginStatus)  {
            marshaller.write(ctx, buffer, value)
        }
    }
}


/**
 * #### Generated from [FathomModel.kt:10]
 */
data class ServerStatus (
    val success: Boolean,
    val port: Int,
    val message: String
) : IPrintable {
    //companion
    
    companion object : IMarshaller<ServerStatus> {
        override val _type: KClass<ServerStatus> = ServerStatus::class
        override val id: RdId get() = RdId(-1286347501835911992)
        
        @Suppress("UNCHECKED_CAST")
        override fun read(ctx: SerializationCtx, buffer: AbstractBuffer): ServerStatus  {
            val success = buffer.readBool()
            val port = buffer.readInt()
            val message = buffer.readString()
            return ServerStatus(success, port, message)
        }
        
        override fun write(ctx: SerializationCtx, buffer: AbstractBuffer, value: ServerStatus)  {
            buffer.writeBool(value.success)
            buffer.writeInt(value.port)
            buffer.writeString(value.message)
        }
        
        
    }
    //fields
    //methods
    //initializer
    //secondary constructor
    //equals trait
    override fun equals(other: Any?): Boolean  {
        if (this === other) return true
        if (other == null || other::class != this::class) return false
        
        other as ServerStatus
        
        if (success != other.success) return false
        if (port != other.port) return false
        if (message != other.message) return false
        
        return true
    }
    //hash code trait
    override fun hashCode(): Int  {
        var __r = 0
        __r = __r*31 + success.hashCode()
        __r = __r*31 + port.hashCode()
        __r = __r*31 + message.hashCode()
        return __r
    }
    //pretty print
    override fun print(printer: PrettyPrinter)  {
        printer.println("ServerStatus (")
        printer.indent {
            print("success = "); success.print(printer); println()
            print("port = "); port.print(printer); println()
            print("message = "); message.print(printer); println()
        }
        printer.print(")")
    }
    //deepClone
    //contexts
    //threading
}
