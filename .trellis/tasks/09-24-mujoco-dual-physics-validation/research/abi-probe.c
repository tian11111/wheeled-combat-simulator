#include <stddef.h>
#include <stdio.h>
#include <mujoco/mujoco.h>

int main(void) {
  printf("mjSolverStat.size=%zu\n", sizeof(mjSolverStat));
  printf("mjWarningStat.size=%zu\n", sizeof(mjWarningStat));
  printf("mjTimerStat.size=%zu\n", sizeof(mjTimerStat));
  printf("mjData.ncon=%zu\n", offsetof(mjData, ncon));
  printf("mjData.energy=%zu\n", offsetof(mjData, energy));
  printf("mjData.qpos=%zu\n", offsetof(mjData, qpos));
  printf("mjData.contact=%zu\n", offsetof(mjData, contact));
  printf("mjContact.size=%zu\n", sizeof(mjContact));
  printf("mjContact.geom0=%zu\n", offsetof(mjContact, geom[0]));
  printf("mjContact.geom1=%zu\n", offsetof(mjContact, geom[1]));
  return 0;
}
