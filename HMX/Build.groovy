pipeline {
    agent any

    environment {
        MSBUILD_PATH = 'C:\\Program Files (x86)\\Microsoft Visual Studio\\2017\\Professional\\MSBuild\\15.0\\Bin\\MSBuild.exe'
		MSBUILD_PATH_EX = 'C:\\Program Files (x86)\\Microsoft Visual Studio\\2017\\Professional\\MSBuild\\15.0\\Bin\\MSBuild.exe'

        COMMAND_LINE_ARGUEMNT = '/t:Clean,Build /property:Configuration=ReleaseVC14 /property:Platform=x64 /p:DebugSymbols=true'
		COMMAND_LINE_ARGUEMNT_EX = '/t:Clean,Build /property:Configuration=ReleaseVC14 /property:Platform=x64 /p:DebugSymbols=true'

        XDORA_BUILD_FILE = './externals/XDORA2/xdora2/xdora2.vcxproj'
        LOBBY_BUILD_FILE = './StdLobbyContent/StdLobbyContent.vcxproj'
        ROOM_BUILD_FILE = './StdPokerRoomContent/StdRoomContent.vcxproj'
        COMMON_BUILD_FILE = './StdCommonContent/StdCommonContent.vcxproj'
        TOURNAMENT_BUILD_FILE = './StdTournamentContent/StdTournamentContent.vcxproj'
		RANKING_BUILD_FILE = './StdRankingContent/StdRankingContent.vcxproj'
		GUILD_BUILD_FILE = './StdGuildCenterContent/StdGuildCenterContent.vcxproj'
        MPAGENT_BUILD_FILE = './MPAgent/MPAgent.vcxproj'
    }

    stages {

        stage('SetUp') {
            steps {
                script {
                    wrap([$class: 'BuildUser']) {
                        currentBuild.displayName = "#${BUILD_NUMBER}(${BUILD_USER_ID}) : ${JOB_NAME} - XDora(true), Lobby(${Lobby}), Room(${Room}), Common(${Common}), Tournament(${Tournament}), Ranking(${Ranking}), Guild(${Guild}), MPAgent(${MPAgent}) - Export (${Export1}), (${Export2}), (${Export3})"
                    }
                }
            }
        }

        stage('Checkout') {
            steps {
                // 선택된 브랜치에서 코드 체크아웃
                git branch: params.Branch+params.Hotfix, url: 'https://github.nhnent.com/qpoker/GameServer/', credentialsId: 'MPJenkins'
            }
        }

        stage('Build Xdora') {
            steps {
                bat """
                chcp 65001
                echo "Building Xdora"

				if "%Export3%" == "Real" (
                    echo Building Xdora Real
                    "%MSBUILD_PATH%" "%XDORA_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
                ) else (
                    echo Building Xdora EX
                    "%MSBUILD_PATH_EX%" "%XDORA_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
                )
                """
            }
        }

        stage('Build Projects') {
            parallel failfast: true, {
                stage('Lobby') {
                    when {
                        expression { return params.Lobby }
                    }
                    stages {
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building Lobby"

								if "%Export3%" == "Real" (
									echo "Building Lobby Real"
									 "%MSBUILD_PATH%" "%LOBBY_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building Lobby EX"
									"%MSBUILD_PATH_EX%" "%LOBBY_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
                                """
                            }
                        }
                    }
                }

                stage('Room') {
                    when {
                        expression { return params.Room }
                    }
                    stages {
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building Room"

                                if "%Export3%" == "Real" (
									echo "Building Room Real"
									 "%MSBUILD_PATH%" "%ROOM_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building Room EX"
									"%MSBUILD_PATH_EX%" "%ROOM_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
								"""
                            }
                        }
                    }
                }

                stage('Common') {
                    when {
                        expression { return params.Common }
                    }
                    stages {
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building Common"

								if "%Export3%" == "Real" (
									echo "Building Common Real"
									 "%MSBUILD_PATH%" "%COMMON_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building Common EX"
									"%MSBUILD_PATH_EX%" "%COMMON_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
                                """
                            }
                        }
                    }
                }

                stage('Tournament'){
                    when {
                        expression { return params.Tournament }
                    }
                    stages{
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building Tournament"

								if "%Export3%" == "Real" (
									echo "Building Tournament Real"
									 "%MSBUILD_PATH%" "%TOURNAMENT_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building Tournament EX"
									"%MSBUILD_PATH_EX%" "%TOURNAMENT_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
                                """
                            }
                        }
                    }
                }

				stage('Ranking') {
                    when {
                        expression { return params.Ranking }
                    }
                    stages {
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building Ranking"

								if "%Export3%" == "Real" (
									echo "Building Ranking Real"
									 "%MSBUILD_PATH%" "%RANKING_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building Ranking EX"
									"%MSBUILD_PATH_EX%" "%RANKING_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
                                """
                            }
                        }
                    }
                }

                stage('Guild'){
                    when {
                        expression { return params.Guild }
                    }
                    stages{
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building Guild"

								if "%Export3%" == "Real" (
									echo "Building Guild Real"
									 "%MSBUILD_PATH%" "%GUILD_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building Guild EX"
									"%MSBUILD_PATH_EX%" "%GUILD_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
                                """
                            }
                        }
                    }
                }

                stage('MPAgent'){
                    when {
                        expression { return params.MPAgent }
                    }
                    stages{
                        stage('Build') {
                            steps {
                                bat """
                                chcp 65001
                                echo "Building MPAgent"

								if "%Export3%" == "Real" (
									echo "Building MPAgent Real"
									 "%MSBUILD_PATH%" "%MPAGENT_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT%
								) else (
									echo "Building MPAgent EX"
									"%MSBUILD_PATH_EX%" "%MPAGENT_BUILD_FILE%" %COMMAND_LINE_ARGUEMNT_EX%
								)
                                """
                            }
                        }
                    }
                }
            }
        }

        stage('Export') {
            steps{
                script {
                    wrap([$class: 'BuildUser']) {
                        exportCmd()
                    }
                }
            }
        }
    }
}

def exportCmd() {
    withCredentials([gitUsernamePassword(credentialsId: 'MPJenkins', gitToolName: 'Default')]) {
        bat """
        chcp 65001
        @echo off

		set HMX_BUILD=C:\\Jenkins\\.jenkins\\workSpace\\SP_GIT
        set HMX_ALPHA=C:\\Jenkins\\.jenkins\\workSpace\\SP_HMX\\HMX_ALPHA

        if exist "%HMX_ALPHA%\\.git" (
            git -C "%HMX_ALPHA%" pull origin main
        ) else (
            git clone https://github.nhnent.com/qpoker/HMX_ALPHA.git "%HMX_ALPHA%"
        )

		set BRANCH_DIR=
        
        for /f "tokens=1 delims=/" %%D IN ("%Branch%%Hotfix%") DO set BRANCH_DIR=%%D
        for /f "tokens=2 delims=/" %%D IN ("%Branch%%Hotfix%") DO set BRANCH_DIR=%BRANCH_DIR%\\%%D
        
        echo %HMX_BUILD%\\%BRANCH_DIR%
        
        if "%Export1%" == "Dev" (
        
        xcopy /Y /S /Q %HMX_BUILD%\\StdLobbyContent\\bin\\x64\\StdLobbyContent.* %HMX_ALPHA%\\MobilePoker_Lobby\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdPokerRoomContent\\bin\\x64\\StdRoomContent.* %HMX_ALPHA%\\MobilePoker_Room\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdTournamentContent\\bin\\x64\\StdTournamentContent.* %HMX_ALPHA%\\MobilePoker_Tournament\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdGuildCenterContent\\bin\\x64\\StdGuildCenterContent.* %HMX_ALPHA%\\MobilePoker_Guild\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdCommonContent\\bin\\x64\\StdCommonContent.* %HMX_ALPHA%\\MobilePoker_Common\\
		xcopy /Y /S /Q %HMX_BUILD%\\StdRankingContent\\bin\\x64\\StdRankingContent.* %HMX_ALPHA%\\MobilePoker_Ranking\\
        xcopy /Y /S /Q %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent\\
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent\\MPAgentMajor.*
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent\\MPAgentMinor.*
        
		@echo git "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export1%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%"        	
        git -C "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export1%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%" && git -C "%HMX_ALPHA%" push origin main

        )
        
        if "%Export2%" == "QA" (
        
        xcopy /Y /S /Q %HMX_BUILD%\\StdLobbyContent\\bin\\x64\\StdLobbyContent.* %HMX_ALPHA%\\MobilePoker_Lobby_%Export2%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdPokerRoomContent\\bin\\x64\\StdRoomContent.* %HMX_ALPHA%\\MobilePoker_Room_%Export2%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdTournamentContent\\bin\\x64\\StdTournamentContent.* %HMX_ALPHA%\\MobilePoker_Tournament_%Export2%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdGuildCenterContent\\bin\\x64\\StdGuildCenterContent.* %HMX_ALPHA%\\MobilePoker_Guild_%Export2%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdCommonContent\\bin\\x64\\StdCommonContent.* %HMX_ALPHA%\\MobilePoker_Common_%Export2%\\
		xcopy /Y /S /Q %HMX_BUILD%\\StdRankingContent\\bin\\x64\\StdRankingContent.* %HMX_ALPHA%\\MobilePoker_Ranking_%Export2%\\
        xcopy /Y /S /Q %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent_%Export2%\\
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent_%Export2%\\MPAgentMajor.*
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent_%Export2%\\MPAgentMinor.*
        
		@echo git "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export2%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%"        	
        git -C "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export2%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%" && git -C "%HMX_ALPHA%" push origin main

        ) 
		
        if "%Export3%" == "Real" (
        
        xcopy /Y /S /Q %HMX_BUILD%\\StdLobbyContent\\bin\\x64\\StdLobbyContent.* %HMX_ALPHA%\\MobilePoker_Lobby_%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdPokerRoomContent\\bin\\x64\\StdRoomContent.* %HMX_ALPHA%\\MobilePoker_Room_%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdTournamentContent\\bin\\x64\\StdTournamentContent.* %HMX_ALPHA%\\MobilePoker_Tournament_%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdGuildCenterContent\\bin\\x64\\StdGuildCenterContent.* %HMX_ALPHA%\\MobilePoker_Guild_%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdCommonContent\\bin\\x64\\StdCommonContent.* %HMX_ALPHA%\\MobilePoker_Common_%Export3%\\
		xcopy /Y /S /Q %HMX_BUILD%\\StdRankingContent\\bin\\x64\\StdRankingContent.* %HMX_ALPHA%\\MobilePoker_Ranking_%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent_%Export3%\\
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent_%Export3%\\MPAgentMajor.*
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\MobilePoker_MPAgent_%Export3%\\MPAgentMinor.*
        
		@echo git "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export3%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%"        	
        git -C "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export3%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%" && git -C "%HMX_ALPHA%" push origin main

        ) 

		if "%Export3%" == "Test_888" (
        
        xcopy /Y /S /Q %HMX_BUILD%\\StdLobbyContent\\bin\\x64\\StdLobbyContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdPokerRoomContent\\bin\\x64\\StdRoomContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdTournamentContent\\bin\\x64\\StdTournamentContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdGuildCenterContent\\bin\\x64\\StdGuildCenterContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdCommonContent\\bin\\x64\\StdCommonContent.* %HMX_ALPHA%\\%Export3%\\
		xcopy /Y /S /Q %HMX_BUILD%\\StdRankingContent\\bin\\x64\\StdRankingContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\%Export3%\\MPAgentMajor.*
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\%Export3%\\MPAgentMinor.*
        
		@echo git "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export3%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%"        	
        git -C "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export3%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%" && git -C "%HMX_ALPHA%" push origin main

        )

		if "%Export3%" == "Test_999" (
        
        xcopy /Y /S /Q %HMX_BUILD%\\StdLobbyContent\\bin\\x64\\StdLobbyContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdPokerRoomContent\\bin\\x64\\StdRoomContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdTournamentContent\\bin\\x64\\StdTournamentContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdGuildCenterContent\\bin\\x64\\StdGuildCenterContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\StdCommonContent\\bin\\x64\\StdCommonContent.* %HMX_ALPHA%\\%Export3%\\
		xcopy /Y /S /Q %HMX_BUILD%\\StdRankingContent\\bin\\x64\\StdRankingContent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /S /Q %HMX_BUILD%\\\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\%Export3%\\
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\%Export3%\\MPAgentMajor.*
        xcopy /Y /F %HMX_BUILD%\\MPAgent\\bin\\x64\\MPAgent.* %HMX_ALPHA%\\%Export3%\\MPAgentMinor.*
        
		@echo git "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export3%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%"        	
        git -C "%HMX_ALPHA%" commit -a -m "%JOB_NAME%\\%BRANCH_DIR% -> HMX(%Export3%) : #%BUILD_NUMBER%( %BUILD_USER_ID% ) - %BUILD_URL%" && git -C "%HMX_ALPHA%" push origin main

        )

		"""
    }
}
