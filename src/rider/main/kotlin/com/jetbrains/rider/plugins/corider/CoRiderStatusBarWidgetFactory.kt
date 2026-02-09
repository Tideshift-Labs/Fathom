package com.jetbrains.rider.plugins.corider

import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.openapi.wm.StatusBarWidgetFactory
import kotlinx.coroutines.CoroutineScope

class CoRiderStatusBarWidgetFactory : StatusBarWidgetFactory {

    override fun getId(): String = "CoRiderStatusBarWidget"

    override fun getDisplayName(): String = "CoRider"

    override fun createWidget(project: Project, scope: CoroutineScope): StatusBarWidget {
        return CoRiderStatusBarWidget(project)
    }

    override fun isAvailable(project: Project): Boolean = true
}
