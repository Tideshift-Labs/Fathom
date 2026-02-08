package com.jetbrains.rider.plugins.corider

import com.intellij.openapi.components.*
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.ui.dsl.builder.bindIntText
import com.intellij.ui.dsl.builder.panel
import com.jetbrains.rd.ide.model.coRiderModel
import com.jetbrains.rider.projectView.solution

@Service
@State(name = "CoRiderSettings", storages = [Storage("CoRiderSettings.xml")])
class CoRiderSettings : SimplePersistentStateComponent<CoRiderSettings.State>(State()) {

    class State : BaseState() {
        var port by property(19876)
    }
}

class CoRiderConfigurable : BoundConfigurable("CoRider") {
    private val settings get() = service<CoRiderSettings>()

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
                project.solution.coRiderModel.port.set(newPort)
            }
        }
    }
}
