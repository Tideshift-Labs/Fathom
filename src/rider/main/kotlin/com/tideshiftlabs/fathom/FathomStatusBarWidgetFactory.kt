package com.tideshiftlabs.fathom

import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.openapi.wm.StatusBarWidgetFactory
import kotlinx.coroutines.CoroutineScope

class FathomStatusBarWidgetFactory : StatusBarWidgetFactory {

    override fun getId(): String = "FathomStatusBarWidget"

    override fun getDisplayName(): String = "Fathom"

    override fun createWidget(project: Project, scope: CoroutineScope): StatusBarWidget {
        return FathomStatusBarWidget(project)
    }

    override fun isAvailable(project: Project): Boolean = true
}
