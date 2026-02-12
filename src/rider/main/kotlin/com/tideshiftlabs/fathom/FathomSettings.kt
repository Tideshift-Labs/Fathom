package com.tideshiftlabs.fathom

import com.intellij.openapi.components.*
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.ui.dsl.builder.bindIntText
import com.intellij.ui.dsl.builder.panel
import com.jetbrains.rd.ide.model.fathomModel
import com.jetbrains.rider.projectView.solution

@Service
@State(name = "FathomSettings", storages = [Storage("FathomSettings.xml")])
class FathomSettings : SimplePersistentStateComponent<FathomSettings.State>(State()) {

    class State : BaseState() {
        var port by property(19876)
    }
}

class FathomConfigurable : BoundConfigurable("Fathom") {
    private val settings get() = service<FathomSettings>()

    override fun createPanel() = panel {
        group("HTTP Server") {
            row("Port:") {
                intTextField(1..65535)
                    .bindIntText(settings.state::port)
            }
        }
    }

    override fun apply() {
        super.apply()
        // Push updated port to all open projects via the RD model
        val newPort = settings.state.port
        for (project in ProjectManager.getInstance().openProjects) {
            if (!project.isDisposed) {
                project.solution.fathomModel.port.set(newPort)
            }
        }
    }
}
