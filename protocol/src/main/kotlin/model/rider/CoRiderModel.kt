package model.rider

import com.jetbrains.rd.generator.nova.*
import com.jetbrains.rd.generator.nova.PredefinedType.*
import com.jetbrains.rider.model.nova.ide.SolutionModel

@Suppress("unused")
object CoRiderModel : Ext(SolutionModel.Solution) {

    private val ServerStatus = structdef {
        field("success", bool)
        field("port", int)
        field("message", string)
    }

    init {
        property("port", int)
        signal("serverStatus", ServerStatus)
    }
}
