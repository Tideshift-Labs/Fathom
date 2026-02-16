package com.tideshiftlabs.fathom

import com.intellij.build.BuildDescriptor
import com.intellij.build.events.*
import com.intellij.execution.process.ProcessOutputType
import com.intellij.execution.process.ProcessOutputTypes

/**
 * Custom implementations of IntelliJ Build event interfaces.
 * These replace the *Impl classes from com.intellij.build.events.impl
 * which are marked @ApiStatus.Internal and flagged by plugin verification.
 */

internal class FathomStartBuildEvent(
    private val descriptor: BuildDescriptor,
    private val msg: String
) : StartBuildEvent {
    override fun getId(): Any = descriptor.id
    override fun getParentId(): Any? = null
    override fun getEventTime(): Long = descriptor.startTime
    override fun getMessage(): String = msg
    override fun getHint(): String? = null
    override fun getDescription(): String? = null
    override fun getBuildDescriptor(): BuildDescriptor = descriptor
}

internal class FathomOutputBuildEvent(
    private val id: Any,
    private val msg: String,
    private val stdOut: Boolean
) : OutputBuildEvent {
    override fun getId(): Any = id
    override fun getParentId(): Any? = null
    override fun getEventTime(): Long = System.currentTimeMillis()
    override fun getMessage(): String = msg
    override fun getHint(): String? = null
    override fun getDescription(): String? = null
    @Deprecated("Use getOutputType() instead", replaceWith = ReplaceWith("getOutputType()"))
    override fun isStdOut(): Boolean = stdOut
    override fun getOutputType(): ProcessOutputType =
        (if (stdOut) ProcessOutputTypes.STDOUT else ProcessOutputTypes.STDERR) as ProcessOutputType
}

internal class FathomFinishBuildEvent(
    private val id: Any,
    private val parent: Any?,
    private val time: Long,
    private val msg: String,
    private val eventResult: EventResult
) : FinishBuildEvent {
    override fun getId(): Any = id
    override fun getParentId(): Any? = parent
    override fun getEventTime(): Long = time
    override fun getMessage(): String = msg
    override fun getHint(): String? = null
    override fun getDescription(): String? = null
    override fun getResult(): EventResult = eventResult
}

internal object FathomSuccessResult : SuccessResult {
    override fun isUpToDate(): Boolean = false
    override fun getWarnings(): List<Warning> = emptyList()
}

internal object FathomFailureResult : FailureResult {
    override fun getFailures(): List<Failure> = emptyList()
}
