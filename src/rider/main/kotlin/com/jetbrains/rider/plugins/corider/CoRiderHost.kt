package com.jetbrains.rider.plugins.corider

import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.jetbrains.rd.ide.model.coRiderModel
import com.jetbrains.rider.projectView.solution

class CoRiderHost : ProjectActivity {

    override suspend fun execute(project: Project) {
        val model = project.solution.coRiderModel

        // Push persisted port to the backend
        val settings = service<CoRiderSettings>()
        model.port.set(settings.state.port)
    }
}
