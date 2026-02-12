package model.rider

import com.jetbrains.rd.generator.nova.*
import com.jetbrains.rd.generator.nova.PredefinedType.*
import com.jetbrains.rider.model.nova.ide.SolutionModel

@Suppress("unused")
object FathomModel : Ext(SolutionModel.Solution) {

    private val ServerStatus = structdef {
        field("success", bool)
        field("port", int)
        field("message", string)
    }

    private val CompanionPluginStatus = enum("CompanionPluginStatus") {
        +"NotInstalled"
        +"Outdated"
        +"Installed"
        +"UpToDate"
    }

    private val CompanionPluginInfo = structdef {
        field("status", CompanionPluginStatus)
        field("installedVersion", string)
        field("bundledVersion", string)
        field("message", string)
    }

    init {
        property("port", int)
        signal("serverStatus", ServerStatus)
        sink("companionPluginStatus", CompanionPluginInfo)
        source("installCompanionPlugin", void)
        source("buildCompanionPlugin", void)
        signal("mcpConfigStatus", string)
    }
}
