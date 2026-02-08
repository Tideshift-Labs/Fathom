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
 * #### Generated from [CoRiderModel.kt:8]
 */
class CoRiderModel private constructor(
    private val _port: RdOptionalProperty<Int>,
    private val _serverStatus: RdSignal<ServerStatus>
) : RdExtBase() {
    //companion
    
    companion object : ISerializersOwner {
        
        override fun registerSerializersCore(serializers: ISerializers)  {
            val classLoader = javaClass.classLoader
            serializers.register(LazyCompanionMarshaller(RdId(-1286347501835911992), classLoader, "com.jetbrains.rd.ide.model.ServerStatus"))
        }
        
        
        
        
        
        const val serializationHash = -5530726814228802747L
        
    }
    override val serializersOwner: ISerializersOwner get() = CoRiderModel
    override val serializationHash: Long get() = CoRiderModel.serializationHash
    
    //fields
    val port: IOptProperty<Int> get() = _port
    val serverStatus: ISignal<ServerStatus> get() = _serverStatus
    //methods
    //initializer
    init {
        _port.optimizeNested = true
    }
    
    init {
        bindableChildren.add("port" to _port)
        bindableChildren.add("serverStatus" to _serverStatus)
    }
    
    //secondary constructor
    internal constructor(
    ) : this(
        RdOptionalProperty<Int>(FrameworkMarshallers.Int),
        RdSignal<ServerStatus>(ServerStatus)
    )
    
    //equals trait
    //hash code trait
    //pretty print
    override fun print(printer: PrettyPrinter)  {
        printer.println("CoRiderModel (")
        printer.indent {
            print("port = "); _port.print(printer); println()
            print("serverStatus = "); _serverStatus.print(printer); println()
        }
        printer.print(")")
    }
    //deepClone
    override fun deepClone(): CoRiderModel   {
        return CoRiderModel(
            _port.deepClonePolymorphic(),
            _serverStatus.deepClonePolymorphic()
        )
    }
    //contexts
    //threading
    override val extThreading: ExtThreadingKind get() = ExtThreadingKind.Default
}
val Solution.coRiderModel get() = getOrCreateExtension("coRiderModel", ::CoRiderModel)



/**
 * #### Generated from [CoRiderModel.kt:10]
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
