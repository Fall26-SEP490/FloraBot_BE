# ==============================================================================
# FLORABOT - SCRIPT SETUP MOSQUITTO MQTT & IIS BACKEND CHO VPS WINDOWS SERVER
# ==============================================================================
$ErrorActionPreference = 'Stop'
Write-Host ">>> Dang khoi tao thu muc va ghi file chung chi MQTT..." -ForegroundColor Cyan

New-Item -ItemType Directory -Force "C:\FloraBot\secrets\mqtt\broker" | Out-Null
New-Item -ItemType Directory -Force "C:\FloraBot\secrets\mqtt\devices" | Out-Null
New-Item -ItemType Directory -Force "C:\FloraBot\data\mosquitto" | Out-Null

[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\backend.json", [Convert]::FromBase64String("ew0KICAiZmxvcmFib3QtYmFja2VuZCI6IHsNCiAgICAicGFzc3dvcmQiOiAiNThCRDlDQzVCREU5ODdFQzMxM0RDOUZBNjkyMUZEODdFRkJGMkIyRTk3QTVGRkRBNkFEMjVBRUFCM0E0QTQ1RiINCiAgfSwNCiAgIkVTUDMyLUExQjJDMyI6IHsNCiAgICAiaG1hY0tleSI6ICI0NkVERjlGNjVBNUNBRTM1RTcwQzhDN0QxM0I2M0FGQUFFMkNFQTk4RkIyRURCQzE0MDZBNEU4Mzc2MjBCMTAxIg0KICB9LA0KICAiRVNQMzItRDRFNUY2Ijogew0KICAgICJobWFjS2V5IjogIjg2QUEzQURCMTFEQTdDOTA0QUMwRDNGNjA0OTUyQ0MyNkY4N0EzMjMyMDE4QkExRUJFN0MwRUU0ODZDMzlCMDgiDQogIH0NCn0="))
[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\broker\ca.crt", [Convert]::FromBase64String("LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSUVJekNDQW91Z0F3SUJBZ0lVY29GYTNCaXRuQzNGbXpwVEh0Uzh5alhXY0lJd0RRWUpLb1pJaHZjTkFRRUwKQlFBd0lURWZNQjBHQTFVRUF3d1dSbXh2Y21GQ2IzUWdURzlqWVd3Z1RWRlVWQ0JEUVRBZUZ3MHlOakV3TVRJdwpPVFU0TVRsYUZ3MHlOekV3TVRJd09UVTRNVGxhTUNFeEh6QWRCZ05WQkFNTUZrWnNiM0poUW05MElFeHZZMkZzCklFMVJWRlFnUTBFd2dnR2lNQTBHQ1NxR1NJYjNEUUVCQVFVQUE0SUJqd0F3Z2dHS0FvSUJnUUNnVjY4Yk96SXMKYU55QTRXQzNnRENGbVNmalVkaXhERGNSR291SXo5bit2VXREZFJWTDhhMUNJYTBHdXo4ZVhlRERic21odmZYaQpvQk5IWkNMWmlMaE5hbjJKZTdLTDByTk1hV2IrSEJES08wekdETjFrWVRESkRJUS9lV2V1VkVSdXZCT0VkUjBrCld4OWVIVldVQzFHaDJrS1dlVDB1TmVhcEQ1bkErbm5MZTRkSW96QWNnQzFWVVFnRDdqT3lRNUlTZHdtdU9tWDkKdWpyNVNvM01Fb3dsSC9LcjB5VWhtQmxEQXJEb0lXb2FtMm5BWXUwRWFiTGhMWTRnZFYyVGN5blV2NDAwZk9oWQpCTFR3VDQ0dzRZYUtLNGptb096bDhYcWhJM280TlFzZUgvd2xOVE9yVDJhaHFqbTNSek5kdjh3NEdHRytkaXBuClRmWUZ0ODFVWUsvVElCTkNtSEZYL0ZEKy95VXRxMjdCc2xTckZNVlpGUGdka2Rsa2NtTFBsREx3emc4L2s1MXgKbDVHeE9PN05BbkFJaVhINFNTMDJCQndYRFFTOVBKa3kzNjBRSTBZdzVDMzR1N0dwbUNxSUNZbnlGQlhQdVRSTwo1UmZRZDdKaGZUZ2NwNG41OW9WSm10aXI2VWgvTjNmb2RmYWpDSUYvTjUzalA2dW1OU1RPYlVVQ0F3RUFBYU5UCk1GRXdIUVlEVlIwT0JCWUVGT1BMV1RGcG9nUCtwcUtkWkZBQi8zTEkvY3pZTUI4R0ExVWRJd1FZTUJhQUZPUEwKV1RGcG9nUCtwcUtkWkZBQi8zTEkvY3pZTUE4R0ExVWRFd0VCL3dRRk1BTUJBZjh3RFFZSktvWklodmNOQVFFTApCUUFEZ2dHQkFDYmdFbnR6MWFnR2FMUTd3SFc5VUx3eGRkN3ZoUkovZkwvUjgxL0IrNW0yNDJtVFB4OFBwSzRTClVwYUVRRVNra2ZMWEx4R0RQMzk4TVhXNTBYTTFDUEFvMHlEYXdiNFRwZmVpSUNCYWpKcFVxUy9yVW0zRFdSWW0KVmdNczh4NTFZakFnUXRlcnJ1aDkwTXdyeDJuRWJMM1BTSHlpRnNsb2JhYkQ1bWJBQk02bGxObzN4ODNSWE9ZVQp3dWhQVklOQkdZV01IdkV5YWIvYzZqVzR4K0d3NHFZK1p2b2QvZnhqV253YTR1Z3NzNTFnenFZRGJ0UUZ1ZXlkCnREWFV3RTZMY0dNeXhLU0JRYjRITVUxSWVrM2tHWlphRkw3U0d6T2poV3Ewd08vSkdUb2hLSGRaQURFbE1jZTcKakh2aWM4b2VqR0lXUVp1VXhyY0FZcDZLWUxidjMvSXlMajZZVittc2RaOTBGbTZ6Tkhrd1JqMTVyVHlRbGpNMgpNb09xMHI2V3pVWFFHSDJxTFpaTTlCV1pUZWVpS3JzYVBCczFFUFhZOGdJWThubFhTVVdoRTFGMkhpSEtKRHJXCkdLSWtTb1VGSVZHUkdxa0g1SXAvTU1EajNaV2Y0enl5NFpSdFRjakxucDFqOXBLV2hqVndEdzFOanN5Ny9Oa1gKZ042VmNkNEh2UT09Ci0tLS0tRU5EIENFUlRJRklDQVRFLS0tLS0K"))
[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\broker\server.crt", [Convert]::FromBase64String("LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSUVjekNDQXR1Z0F3SUJBZ0lVWG9xZzNjampNQ0JSK3RHVysxT05ncThsdklJd0RRWUpLb1pJaHZjTkFRRUwKQlFBd0lURWZNQjBHQTFVRUF3d1dSbXh2Y21GQ2IzUWdURzlqWVd3Z1RWRlVWQ0JEUVRBZUZ3MHlOakV3TWpndwpOakkwTkRKYUZ3MHlPREV3TWpjd05qSTBOREphTUJ3eEdqQVlCZ05WQkFNTUVXRndhUzVtYkc5eVlXSnZkQzV6CmFYUmxNSUlCb2pBTkJna3Foa2lHOXcwQkFRRUZBQU9DQVk4QU1JSUJpZ0tDQVlFQTRocDltMFVuVXVrSlY3QVQKZ0NtOG5CNFVxcEZtakNSelFPT005a0t4cTBXRzExS1NZeGdwWCsyR3BXVW1OaVVRWjN5THB0eGxuZ05PbWx2RAozSTUxcUxRWmRmZnltQmhCZW1LbkplYnAzQ2w5UjkwT0ZZMkljbzIyQVFSU2tYWldRazdiWTN1V3VtSFFRZVc4CmFFVmY3d3N0S2dCaHF2OWtrUmJyOHRIcWFPUkEvTFJhOHk1NkY0TDhtc2lNRkNPK084WmtLZFlPNkVld0E5T20KMW1BUkw1SEplUExUak1qWll4RGRMaWhYK1pETEdKeUhIZG1XOWRaYXo0clJYaFl2OHpkNWt1R1I2TkczY2tSeQpPa3liY1VPVllmY2VGSmo5ZG5CNkJIU2NuYjQ2Nm9SdzVKUmRPYjF3QktrN2xVNitSMXl5NVNjdzg0OTlrNGhUClBZOWFmd0ZXc25abXJzS3FTMW41Q2VpVElkMGozTno4NkdQNVhuSE83WlZ3dEh2WjIyWnk5MlVobkM2cS9LUGIKdEhycE5qWVR6UVVRNnd4ZGV0ejdkUXc4bjU1MkRiRy9xRDF6ci9JTE94anVMbDUzL3QyNHpEUGpZQ0x4N2Q0ZgpMMElmM082TXh3V3VLYzJkbmJQemZib3I3Y0E2MytTUmhyNXJmdWFZeWRRdi96OEJBZ01CQUFHamdhY3dnYVF3ClRRWURWUjBSQkVZd1JJSVJZWEJwTG1ac2IzSmhZbTkwTG5OcGRHV0NEV1pzYjNKaFltOTBMbk5wZEdXQ0NXMXYKYzNGMWFYUjBiNElKYkc5allXeG9iM04waHdRa01vZUdod1IvQUFBQk1CTUdBMVVkSlFRTU1Bb0dDQ3NHQVFVRgpCd01CTUIwR0ExVWREZ1FXQkJRK1BtR3NYREQxcDBoaldBTDdpSkVsT1ZzZEFqQWZCZ05WSFNNRUdEQVdnQlRqCnkxa3hhYUlEL3FhaW5XUlFBZjl5eVAzTTJEQU5CZ2txaGtpRzl3MEJBUXNGQUFPQ0FZRUFEUkFxUGI2NEJQSm0KSVBtOE9Ud1diOHRHeVRuV2ZpSDRPTFU4THNLdGtVOC95S2ZJZXFGcGdoZzJvajZvWWxrSmhxYmRiLzhKa2luSgo3N0orelY3UThzc1hqYy9SbnhyYjlGeTdQS3pHeDc3VkpPKzc4UHA5R1B5WHgwR3czMVkraDl3WmRqVnNtRXBlCm8ycU1ZK3pnT0tsNzRzQ3lHL2NOSkhSTDM4VVcwcWdKZUNvd2cvU0FDV0x5OVJvbElwdTdxMC9UeEM0RTFxQ2YKVERPQkNja1FVREh5bXQ0eFVGWUEzMWkzckwyTi92Nkh2d1U5WkZtWDdockdNYjRCMGovMzBDRWRVSit1SUVRSQphcU9pbGthT3YwNy9tUWZnNnVDTnFMc09NaGJKcFpvRWdveGYxMmQ5djgxNWswdVpPdWVyQi9MekR0d09mSnd6CjV6V1hvcHNzRjNmVmpxU0NZRlJMV0ZwUkduREE3S2JPLzdaekROVzVvcFNyblFPcmpkdUQyYjE5T0R6Q2dTVzYKY1o5OGF1VTJVUkVOMTVja3dFMktPYlhwL25sZC9aL0hJRC9Db09CNWdqM2VzeDFXS0hQSDVwQkw4aWJDUEs0NAphM1FjOU11eTNaaEc4WXdscEQzSjY5dXhlenZXcDBUaVV6KzY5ZmFkQ3RBcXNFS2dyYlVTCi0tLS0tRU5EIENFUlRJRklDQVRFLS0tLS0K"))
[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\broker\server.key", [Convert]::FromBase64String("LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0tCk1JSUcvZ0lCQURBTkJna3Foa2lHOXcwQkFRRUZBQVNDQnVnd2dnYmtBZ0VBQW9JQmdRRGlHbjJiUlNkUzZRbFgKc0JPQUtieWNIaFNxa1dhTUpITkE0NHoyUXJHclJZYlhVcEpqR0NsZjdZYWxaU1kySlJCbmZJdW0zR1dlQTA2YQpXOFBjam5Xb3RCbDE5L0tZR0VGNllxY2w1dW5jS1gxSDNRNFZqWWh5amJZQkJGS1JkbFpDVHR0amU1YTZZZEJCCjVieG9SVi92Q3kwcUFHR3EvMlNSRnV2eTBlcG81RUQ4dEZyekxub1hndnlheUl3VUk3NDd4bVFwMWc3b1I3QUQKMDZiV1lCRXZrY2w0OHRPTXlObGpFTjB1S0ZmNWtNc1luSWNkMlpiMTFsclBpdEZlRmkvek4zbVM0WkhvMGJkeQpSSEk2VEp0eFE1Vmg5eDRVbVAxMmNIb0VkSnlkdmpycWhIRGtsRjA1dlhBRXFUdVZUcjVIWExMbEp6RHpqMzJUCmlGTTlqMXAvQVZheWRtYXV3cXBMV2ZrSjZKTWgzU1BjM1B6b1kvbGVjYzd0bFhDMGU5bmJabkwzWlNHY0xxcjgKbzl1MGV1azJOaFBOQlJEckRGMTYzUHQxRER5Zm5uWU5zYitvUFhPdjhnczdHTzR1WG5mKzNiak1NK05nSXZIdAozaDh2UWgvYzdvekhCYTRweloyZHMvTjl1aXZ0d0RyZjVKR0d2bXQrNXBqSjFDLy9Qd0VDQXdFQUFRS0NBWUF2CnJ2a0c3aVJmTEtUTEFLZjRLelVxOG9BV3diL1A5ZDcxbEJlMkZyWXNBZWR3VVNwc3FodU5yc1RxN0NSTkhtOXgKZDdkbSt3dVdNYW5aelZrRzBtZDNhdllFcGtRYjlkK1hQU2QxaTVlTjFoVWsrdlpkcnNIVU1OVXRyVGp4a3ZTWgptdEpUVG16cEJUaGJzV1RVMDY2cDllQXlQNjdaVm0raDFxVHZpbndtWDE3aVBISUMzNjRoZDc4b3NIczBReDU3Ck1qbU5CaWNTTHc0c25HNE92MmRZY2lVOGMrK1AvUXExaFFpMFU0OU1JMUJsc2hzZnZUUUVTQTdTUHJzdFhiMmQKQThySHhOR0EyeG1sYVlGNHFxT08rZ1U1c0FTSDZoMHZjVzlEWFl6aGs1SHB0MExCczA2cHpyQU9pbkpvdHMyVgpJaHVTdmUwMFFIWm9lZm53OXZrdWNwcnUydU5xT3hNSlRmYjlIWVBGUGV3RnpGY3liZ2djYmgxcWFpcUNxWk9yCjVzZkVKSlgvc0lZUS8wNVVVdGJnbXR3SjBvaXUyeXVUVGRGNFo0QjR2ZFY1Mk5uUmFBTVprcitmUitwRElnbFEKL0ZpSlFHNHJKSFIyUEhDK3A0ZEt5M1lDOWlJWGZ0TURVQlJZZlpqdVRtSVhiRXl5VDE4azJDN21scHZOOXYwQwpnY0VBK0tia1dmWlZTNEdDMkwzZXEwNDRWeWtNNEZCOEJ6YkdqL1NGRXdtSnZLd2FFdUthTmMvZHBrOERGeDh1ClVONm9aUGxGemRzNmJEaGJ5cFhyb01COStlQ2l1SC9BTWROTUQ5MkpPUStCaGV3RlpYcCs1dU5BVmJRU2ROaWQKcFJ6dCtXMEQwcU40ZGRHSExOUUg1cEVVV0lPOFhBVWZRbXhHaGxXUWI4NE9iQUpna0NBc2NGOFk1eTArcitFZwpXdDZGUmRrc2k2TW9SWTZBcE5hb1QvV3pFVmo0QXFzVlZnSkpxSXFKMThFS2RtZmZja2pFSGlNWmZGSjhDVmVXClovdmxBb0hCQU9qSkE3dmpOMXc0UUdoS2RjcEtYWnVsZnpmbFI4TXVWMWRGMzI3bWZ4dFFsZG5ka3I1MFpzOUIKbGZSNG5BWTBOUHY2V0ludEQ1VE91MFFENHZlRWVtak5UeHdnV1J2OFFsMEM0Uis0VkFuMXp6cWFIbjI2SFBMQQpTUzhycDBnSFViQytSRGwwQWljNFZnNjNNUUJFZkJYaVRWcEtTL0FkVUo2eTlXczhaVGVneEJydnZsZXBFMDBGCkpEbzEzMDBOQk5CYVRQWEV5TXkxMFU0eTI0c1dQNTkwZ1Fta2pPOEdBR000RDVHbFp3NVBKaHhubHpoS1VIa3QKNzBaZGNwWWM3UUtCd1FDaEJUS0xMNEk4eXptNHNmd29ZeGNKR29SRUh0cXlzVDRVcXZ3aVArTDZVMWp2TWQ4NwpTY2oza2VNbWJnTElTK2d3dTF3QTJ4OFNSY3pyZS9xQ3R5VGRWQ01MNTI3RzFWWkVUK28xRVYySEpEaTZab0VYCmM4S052WGJXUkJDU2hCRjNuS2tGRnVkREpOU3Nxd0lTeFpDdllWL3hGMEFPKzU4UUR3amgza1pvbTRGODA5anoKVENJTVVyaHRuUlFleEVyNTJKR0lPN2d3a3YrcnYzWnZrV1RNcnhpc1lhNEhiS1BXaVNDS24yL3ZEWVVkdFJHTAprcDlHYXdQMzZNaDF2aUVDZ2NFQW5rM1ZGRGo0dE9Zc0pzQThTazA0UzFqSW95SnhJdktvd3RqcGtXbCtsdnkrCmEyS2JJSm5ZTUtuUmFHZDg4MXNkalI0Nmg5R0cveE4zbGxGdVVCVHZpY1hWUHJOZzVmOHV3a0hyQnI4N3AycGEKZXVmRVJJd0VrN0RnRkF3RHZwLzdsYzVPWnlQVk13YkV2MDBsTGFGbElSeThyZUpJc2dtOWdhOEI1dU16a2hqcQpROGMyRWpjUXdSZjJBd0FQMHo0L1A5R285Sm5XVkNCeExsUzNxR2FBN2RjMHlYSDF4YmEzSE1KTEZDai9aR08rCjd5SWpYRXh0WFhzM2RRcW5taDRoQW9IQVdQN1I1Nm1WYWdWWFFQWkF2VWQ1V2Z3cFN5aysyY2w0bU9hRDQ4bHEKUnRUZ0EvdDNFeHBsTmljRkkzLzg4WStPMzRSakFLYThxelVCZTN1S0hqcHBmUVhnVFY4bEVoalBTejZ3K0ZiVApaL09ZeVdkQTY2aElDWHNUZ0VWNWVBQzNwR1p3RUdzWDBjSXZHOEEwcUl4bC9XUHdzUWtObG5FdGdIb3RoWEZtCm9URUluVGVVem54OVgvNC9xVFBsTW14ZDluS1ZBSFRadHdrWXh2ODJ2SnJpdW8yVUZGek9YZ05wK3Juc3FkZWgKaFBhZkhYaThtNlR6NTVWWXBicWVNcVpVCi0tLS0tRU5EIFBSSVZBVEUgS0VZLS0tLS0K"))
[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\broker\passwords", [Convert]::FromBase64String("RVNQMzItQTFCMkMzOiQ3JDEwMDAkWTRKTXZkWndtSGZOZSs1YnVWTXZ4d3ZuRUd5N0svVE1lN0RCdFM5blk2TUZ4UDlYaVZBSmJmU2lpTzdqbzk0bWRSOCtDMCtFNGN5MlpLTXNqOGh6WXc9PSRtTFlvajhEajBhZkRyZzBrbUl4WUF3WW9paGNocUhPMDUxcktzcjlReUFNNVUwaTY3WHRoN3p3OFB2RVZucFJnVitsa2hKZFRrZlR0cEZuQmROc2VBdz09CkVTUDMyLUQ0RTVGNjokNyQxMDAwJFlZWHdETjc3YzFiSDVDaFM4L1YxaG5Qb09sbElTV1lUS1gyajI1aXVLWjIzdUVabEVtam1uUjEzbHFnY3RLbkhybll4Y1g4VFJTRFd1WXhDUTlSdGhBPT0kQThGTUEzUFJxVUdXU3U2SDJvK1pMODVrbmhrVnlsaEtRclpNZmcyQS9hYUdRU0ZCSlJDRnZTTnlSZEt5Y1o1RFRzZnZybWtKM3NseWtaK1p5WVZEbXc9PQpmbG9yYWJvdC1iYWNrZW5kOiQ3JDEwMDAkR1RSNmtRTVdIemNDUWl0R3FZUll1MUdnS2VjUHJFMlBKVUd0YXY4S1JjWlBKaGRQTmQzYXk3bmR5RDBLUVVqZ1RxOEtmVFo5TGorK3IyZjd6ZjNDbkE9PSRxZ1ZYTEFkTGlMUjFhU2p5U0RnQXdRZFVMRSs5dkRTOThDRFFxYzdtcnhFT0p2a3dINFZQSjF5M25CTnhDZnpWRUFVUU8yaUMrK2EvWmhXc3RJUUdYQT09Cg=="))
[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\broker\acl", [Convert]::FromBase64String("dXNlciBmbG9yYWJvdC1iYWNrZW5kDQp0b3BpYyB3cml0ZSBraW9zay8rL2NtZA0KdG9waWMgcmVhZCBraW9zay8rL2V2dA0KDQp1c2VyIEVTUDMyLUExQjJDMw0KdG9waWMgcmVhZCBraW9zay9FU1AzMi1BMUIyQzMvY21kDQp0b3BpYyB3cml0ZSBraW9zay9FU1AzMi1BMUIyQzMvZXZ0DQoNCnVzZXIgRVNQMzItRDRFNUY2DQp0b3BpYyByZWFkIGtpb3NrL0VTUDMyLUQ0RTVGNi9jbWQNCnRvcGljIHdyaXRlIGtpb3NrL0VTUDMyLUQ0RTVGNi9ldnQNCg0K"))
[IO.File]::WriteAllBytes("C:\FloraBot\secrets\mqtt\devices\ESP32-A1B2C3.json", [Convert]::FromBase64String("ew0KICAiaG1hY0tleSI6ICI0NkVERjlGNjVBNUNBRTM1RTcwQzhDN0QxM0I2M0FGQUFFMkNFQTk4RkIyRURCQzE0MDZBNEU4Mzc2MjBCMTAxIiwNCiAgInBhc3N3b3JkIjogIkUxMDZEODQxNkQ5NDJBNTA1NkVBMjY5NEJFRjA3REIwMTc5RUUwQjk0NjRCRDg5NUI1MjE4QzQ2MjM5RUUyNUYiLA0KICAiaGFyZHdhcmVfaWQiOiAiRVNQMzItQTFCMkMzIg0KfQ=="))

Write-Host ">>> Da tao xong cac file chung chi tai C:\FloraBot\secrets\mqtt" -ForegroundColor Green

# 2. Cai dat Mosquitto neu chua co
if (-not (Test-Path "C:\Program Files\mosquitto\mosquitto.exe")) {
    Write-Host ">>> Dang tai va cai dat Mosquitto cho Windows..." -ForegroundColor Cyan
    $installer = "$env:TEMP\mosquitto-installer.exe"
    Invoke-WebRequest -Uri "https://mosquitto.org/files/binary/win64/mosquitto-2.1.2-install-windows-x64.exe" -OutFile $installer -UseBasicParsing
    Start-Process -FilePath $installer -ArgumentList "/S" -Wait
    Start-Sleep -Seconds 3
}

# 3. Tao file cau hinh mosquitto.conf
$mosqConf = @"
listener 8883 0.0.0.0
allow_anonymous false
password_file C:/FloraBot/secrets/mqtt/broker/passwords
acl_file C:/FloraBot/secrets/mqtt/broker/acl
cafile C:/FloraBot/secrets/mqtt/broker/ca.crt
certfile C:/FloraBot/secrets/mqtt/broker/server.crt
keyfile C:/FloraBot/secrets/mqtt/broker/server.key
tls_version tlsv1.2
persistence true
persistence_location C:/FloraBot/data/mosquitto/
log_dest file C:/FloraBot/data/mosquitto/mosquitto.log
max_packet_size 16384
"@
Set-Content -Path "C:\Program Files\mosquitto\mosquitto.conf" -Value $mosqConf -Encoding ASCII

# 4. Khoi dong Service Mosquitto
Write-Host ">>> Dang cau hinh Windows Service Mosquitto..." -ForegroundColor Cyan
$service = Get-Service -Name mosquitto -ErrorAction SilentlyContinue
if (-not $service) {
    & "C:\Program Files\mosquitto\mosquitto.exe" install
    Start-Sleep -Seconds 2
}
Set-Service -Name mosquitto -StartupType Automatic
Restart-Service -Name mosquitto -Force
Write-Host ">>> Mosquitto Service dang chay!" -ForegroundColor Green

# 5. Mo Windows Firewall cho Port 8883
Write-Host ">>> Dang mo Firewall cho Port 8883..." -ForegroundColor Cyan
New-NetFirewallRule -DisplayName "FloraBot MQTT 8883" -Direction Inbound -LocalPort 8883 -Protocol TCP -Action Allow -ErrorAction SilentlyContinue | Out-Null

# 6. Cau hinh bien moi truong vao AppPool FloraBot-API tren IIS
Write-Host ">>> Dang cap nhat bien moi truong IIS cho FloraBot-API..." -ForegroundColor Cyan
$configPath = "$env:windir\System32\inetsrv\config\applicationHost.config"
[xml]$cfg = Get-Content $configPath -Raw
$pool = $cfg.SelectSingleNode("/configuration/system.applicationHost/applicationPools/add[@name='FloraBot-API']")
$envVars = $pool.SelectSingleNode("environmentVariables")
if (-not $envVars) {
    $envVars = $cfg.CreateElement("environmentVariables")
    $pool.AppendChild($envVars) | Out-Null
}

$variables = @{
    "MQTT_ENABLED" = "true"
    "MQTT_HOST" = "127.0.0.1"
    "MQTT_PORT" = "8883"
    "MQTT_CLIENT_ID" = "florabot-main-api"
    "MQTT_CREDENTIALS_FILE" = "C:\FloraBot\secrets\mqtt\backend.json"
    "MQTT_CA_FILE" = "C:\FloraBot\secrets\mqtt\broker\ca.crt"
}

foreach ($k in $variables.Keys) {
    $node = $envVars.SelectSingleNode("./add[@name='$k']")
    if (-not $node) {
        $node = $cfg.CreateElement("add")
        $node.SetAttribute("name", $k)
        $envVars.AppendChild($node) | Out-Null
    }
    $node.SetAttribute("value", $variables[$k])
}
$cfg.Save($configPath)

# 7. Restart IIS de nhan bien moi
Write-Host ">>> Dang restart IIS de Backend nhan bien MQTT..." -ForegroundColor Cyan
& iisreset

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host "  HOAN TAT! Mosquitto va FloraBot-API da hoat dong MQTT!" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
netstat -ano | findstr "8883"
